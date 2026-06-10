using System;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DBAnalyzer.Models;
using DBAnalyzer.Services;
using Microsoft.Win32;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace DBAnalyzer
{
    public partial class MainWindow : Window
    {
        private readonly DatabaseService _databaseService = new DatabaseService();
        private readonly LlmService _llmService = new LlmService();
        private readonly ObservableCollection<DbConnectionInfo> _connections = new ObservableCollection<DbConnectionInfo>();
        private readonly ObservableCollection<ContextItem> _contextItems = new ObservableCollection<ContextItem>();
        private DataTable? _lastQueryResult;
        private string _lastQueryConnectionName = string.Empty;
        private const string SettingsFile = "appsettings.json";

        public MainWindow()
        {
            InitializeComponent();
            QuestPDF.Settings.License = LicenseType.Community;
            LstConnections.ItemsSource = _connections;
            ContextItemsPanel.ItemsSource = _contextItems;
            LoadSettings();
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("LlmApiUrl", out var urlProp))
                        TxtApiUrl.Text = urlProp.GetString() ?? TxtApiUrl.Text;
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new { LlmApiUrl = TxtApiUrl.Text.Trim() };
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnSaveUrl_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            SetStatus("API URL saved.", isSuccess: true);
        }

        private void BtnAddConnection_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddConnectionDialog { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result != null)
            {
                _connections.Add(dialog.Result);
                LstConnections.SelectedItem = dialog.Result;
            }
        }

        private void BtnRemoveConnection_Click(object sender, RoutedEventArgs e)
        {
            if (LstConnections.SelectedItem is DbConnectionInfo conn)
            {
                _connections.Remove(conn);
                TxtSelectedConnection.Text = "— None selected —";
                _lastQueryResult = null;
                DgResults.ItemsSource = null;
                TxtQueryResultInfo.Text = string.Empty;
            }
        }

        private async void BtnTestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (LstConnections.SelectedItem is not DbConnectionInfo conn)
            {
                MessageBox.Show("Please select a connection to test.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SetBusy("Testing connection...");
            var (success, message) = await _databaseService.TestConnectionAsync(conn);
            SetBusy(null);
            MessageBox.Show(message, success ? "Connection Test" : "Connection Test Failed",
                MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Error);
            SetStatus(message, isSuccess: success);
        }

        private void LstConnections_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstConnections.SelectedItem is DbConnectionInfo conn)
                TxtSelectedConnection.Text = $"{conn.Name}  ({conn.Server} / {conn.Database})";
        }

        private async void BtnRunQuery_Click(object sender, RoutedEventArgs e)
        {
            if (LstConnections.SelectedItem is not DbConnectionInfo conn)
            {
                MessageBox.Show("Please select a database connection.", "No Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var query = TxtQuery.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                MessageBox.Show("Please enter a SQL query.", "Empty Query", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetBusy($"Running query on {conn.Name}...");
            DgResults.ItemsSource = null;
            _lastQueryResult = null;

            var (data, error) = await _databaseService.ExecuteQueryAsync(conn, query);
            SetBusy(null);

            if (!string.IsNullOrEmpty(error))
            {
                TxtQueryResultInfo.Text = $"Error: {error}";
                SetStatus($"Query failed: {error}", isSuccess: false);
                MessageBox.Show(error, "Query Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (data != null)
            {
                _lastQueryResult = data;
                _lastQueryConnectionName = conn.Name;
                DgResults.ItemsSource = data.DefaultView;
                TxtQueryResultInfo.Text = $"{data.Rows.Count} row(s) returned from {conn.Name}";
                SetStatus($"Query executed: {data.Rows.Count} row(s)", isSuccess: true);
            }
        }

        private void BtnAddToContext_Click(object sender, RoutedEventArgs e)
        {
            if (_lastQueryResult == null || _lastQueryResult.Rows.Count == 0)
            {
                MessageBox.Show("No query results to add. Run a query first.", "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var item = _databaseService.DataTableToContextItem(_lastQueryResult, _lastQueryConnectionName);
            _contextItems.Add(item);
            SetStatus($"Added \"{_lastQueryConnectionName}\" to context  ({_contextItems.Count} block(s) total)", isSuccess: true);
        }

        private void BtnClearContext_Click(object sender, RoutedEventArgs e)
        {
            _contextItems.Clear();
            SetStatus("Context cleared.", isSuccess: true);
        }

        private string BuildAggregatedText()
        {
            var sb = new StringBuilder();
            foreach (var item in _contextItems)
                sb.AppendLine(item.RawText);
            return sb.ToString();
        }

        private async void BtnSendToLlm_Click(object sender, RoutedEventArgs e)
        {
            var prompt = TxtPrompt.Text.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                MessageBox.Show("Please enter a prompt.", "Empty Prompt", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var apiUrl = TxtApiUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                MessageBox.Show("Please configure the LLM API URL.", "No API URL", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnSendToLlm.IsEnabled = false;
            BtnExportPdf.IsEnabled = false;
            SetBusy("Sending to LLM...");
            TxtLlmResponse.Text = "Waiting for response...";

            var aggregatedData = BuildAggregatedText();
            var (response, error) = await _llmService.SendMessageAsync(apiUrl, aggregatedData, prompt);

            SetBusy(null);
            BtnSendToLlm.IsEnabled = true;

            if (!string.IsNullOrEmpty(error))
            {
                TxtLlmResponse.Text = $"Error: {error}";
                SetStatus($"LLM call failed: {error}", isSuccess: false);
            }
            else
            {
                TxtLlmResponse.Text = response;
                TxtLlmResponse.ScrollToEnd();
                BtnExportPdf.IsEnabled = true;
                SetStatus("LLM response received.", isSuccess: true);
            }
        }

        private void BtnClearResponse_Click(object sender, RoutedEventArgs e)
        {
            TxtLlmResponse.Text = string.Empty;
            BtnExportPdf.IsEnabled = false;
        }

        private void BtnExportPdf_Click(object sender, RoutedEventArgs e)
        {
            var response = TxtLlmResponse.Text.Trim();
            if (string.IsNullOrWhiteSpace(response))
            {
                MessageBox.Show("No LLM response to export.", "Nothing to Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Save Analysis Report",
                Filter = "PDF Files (*.pdf)|*.pdf",
                FileName = $"Analysis_{DateTime.Now:yyyyMMdd_HHmmss}.pdf",
                DefaultExt = ".pdf"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                SetBusy("Generating PDF...");
                var prompt = TxtPrompt.Text.Trim();
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4);
                        page.Margin(40);
                        page.DefaultTextStyle(t => t.FontSize(11).FontFamily("Arial"));

                        page.Header().Column(col =>
                        {
                            col.Item().Row(row =>
                            {
                                row.RelativeItem().Text("DB Analyzer — Analysis Report")
                                    .FontSize(18).Bold().FontColor("#4A3FBF");
                                row.ConstantItem(120).AlignRight()
                                    .Text(timestamp).FontSize(9).FontColor("#888888");
                            });
                            col.Item().PaddingTop(4).LineHorizontal(1).LineColor("#CCCCCC");
                        });

                        page.Content().PaddingTop(16).Column(col =>
                        {
                            if (!string.IsNullOrWhiteSpace(prompt))
                            {
                                col.Item().Text("Prompt").FontSize(13).Bold().FontColor("#333333");
                                col.Item().PaddingTop(4).PaddingBottom(12)
                                    .Background("#F5F5FA").Padding(10)
                                    .Text(prompt).FontSize(11).FontColor("#444444");
                            }

                            col.Item().Text("Analysis Result").FontSize(13).Bold().FontColor("#333333");
                            col.Item().PaddingTop(4).Text(response).FontSize(11).LineHeight(1.5f);
                        });

                        page.Footer().AlignCenter()
                            .Text(t =>
                            {
                                t.Span("Page ").FontSize(9).FontColor("#AAAAAA");
                                t.CurrentPageNumber().FontSize(9).FontColor("#AAAAAA");
                                t.Span(" / ").FontSize(9).FontColor("#AAAAAA");
                                t.TotalPages().FontSize(9).FontColor("#AAAAAA");
                            });
                    });
                }).GeneratePdf(dlg.FileName);

                SetBusy(null);
                SetStatus($"PDF saved: {dlg.FileName}", isSuccess: true);
                MessageBox.Show($"PDF saved successfully:\n{dlg.FileName}", "Export Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SetBusy(null);
                SetStatus($"PDF export failed: {ex.Message}", isSuccess: false);
                MessageBox.Show($"Failed to generate PDF:\n{ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetBusy(string? message)
        {
            if (message != null)
            {
                TxtStatusBar.Text = message;
                LoadingProgress.Visibility = Visibility.Visible;
            }
            else
            {
                TxtStatusBar.Text = "Ready";
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void SetStatus(string message, bool isSuccess)
        {
            TxtStatusBar.Text = message;
            TxtStatus.Text = message;
            TxtStatus.Foreground = isSuccess
                ? (System.Windows.Media.Brush)FindResource("SuccessColor")
                : (System.Windows.Media.Brush)FindResource("ErrorColor");
        }
    }
}

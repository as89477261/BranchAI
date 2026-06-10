using System;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Linq;
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
        private const string SettingsFile = "appsettings.json";

        // Tracks which connection is currently loaded in the edit form
        private DbConnectionInfo? _editingConnection = null;

        public MainWindow()
        {
            InitializeComponent();
            QuestPDF.Settings.License = LicenseType.Community;
            LstConnections.ItemsSource = _connections;
            ContextItemsPanel.ItemsSource = _contextItems;
            _contextItems.CollectionChanged += (_, _) => UpdateContextSummary();
            LoadSettings();
        }

        // ────────────────────────────────────────────────────────────
        // Settings persistence
        // ────────────────────────────────────────────────────────────

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
                var root = doc.RootElement;
                if (root.TryGetProperty("LlmApiUrl", out var url))
                    TxtApiUrl.Text = url.GetString() ?? TxtApiUrl.Text;

                if (root.TryGetProperty("Connections", out var conns))
                {
                    foreach (var c in conns.EnumerateArray())
                    {
                        _connections.Add(new DbConnectionInfo
                        {
                            Name           = c.GetStringProp("Name"),
                            Server         = c.GetStringProp("Server"),
                            Database       = c.GetStringProp("Database"),
                            Username       = c.GetStringProp("Username"),
                            Password       = c.GetStringProp("Password"),
                            UseWindowsAuth = c.TryGetProperty("UseWindowsAuth", out var wa) && wa.GetBoolean(),
                            SqlQuery       = c.GetStringProp("SqlQuery", "SELECT TOP 100 * FROM "),
                        });
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var obj = new
                {
                    LlmApiUrl   = TxtApiUrl.Text.Trim(),
                    Connections = _connections.Select(c => new
                    {
                        c.Name, c.Server, c.Database,
                        c.Username, c.Password,
                        c.UseWindowsAuth, c.SqlQuery
                    })
                };
                File.WriteAllText(SettingsFile,
                    JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings:\n{ex.Message}", "Warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnSaveUrl_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            SetStatus("API URL saved.", true);
        }

        // ────────────────────────────────────────────────────────────
        // Analysis Tab — Fetch Data
        // ────────────────────────────────────────────────────────────

        private async void BtnFetchData_Click(object sender, RoutedEventArgs e)
        {
            if (_connections.Count == 0)
            {
                MessageBox.Show("No data sources configured.\nGo to the \"Data Sources\" tab to add a connection.",
                    "No Sources", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var missing = _connections.Where(c => string.IsNullOrWhiteSpace(c.SqlQuery)).ToList();
            if (missing.Count == _connections.Count)
            {
                MessageBox.Show("All connections are missing a SQL query.\nEdit them in the \"Data Sources\" tab.",
                    "No Queries", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnFetchData.IsEnabled = false;
            SetBusy($"Fetching data from {_connections.Count} source(s)...");
            _contextItems.Clear();
            TxtEmptyContext.Visibility = Visibility.Collapsed;

            int ok = 0, fail = 0;
            var tasks = _connections
                .Where(c => !string.IsNullOrWhiteSpace(c.SqlQuery))
                .Select(c => FetchOne(c))
                .ToList();

            var results = await Task.WhenAll(tasks);
            foreach (var (item, error) in results)
            {
                if (item != null) { _contextItems.Add(item); ok++; }
                else fail++;
            }

            SetBusy(null);
            BtnFetchData.IsEnabled = true;

            if (_contextItems.Count == 0)
                TxtEmptyContext.Visibility = Visibility.Visible;

            var msg = $"Fetched {ok} source(s) successfully.";
            if (fail > 0) msg += $"  {fail} source(s) failed.";
            SetStatus(msg, fail == 0);
        }

        private async Task<(ContextItem? item, string error)> FetchOne(DbConnectionInfo conn)
        {
            var (data, error) = await _databaseService.ExecuteQueryAsync(conn, conn.SqlQuery);
            if (!string.IsNullOrEmpty(error))
            {
                Dispatcher.Invoke(() =>
                    SetStatus($"Error on {conn.Name}: {error}", false));
                return (null, error);
            }
            if (data == null || data.Rows.Count == 0)
            {
                var empty = new ContextItem
                {
                    Header  = $"{conn.Name}  —  0 rows",
                    RawText = $"[{conn.Name}] No rows returned.\n"
                };
                return (empty, string.Empty);
            }
            return (_databaseService.DataTableToContextItem(data, conn.Name), string.Empty);
        }

        private void UpdateContextSummary()
        {
            if (_contextItems.Count == 0)
            {
                TxtContextSummary.Text = "No data loaded yet. Configure sources in the Data Sources tab.";
                TxtEmptyContext.Visibility = Visibility.Visible;
            }
            else
            {
                int total = _contextItems.Sum(i => i.Rows.Count);
                TxtContextSummary.Text = $"{_contextItems.Count} source(s) loaded  ·  {total} total row(s)";
                TxtEmptyContext.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnClearContext_Click(object sender, RoutedEventArgs e)
        {
            _contextItems.Clear();
            SetStatus("Context cleared.", true);
        }

        // ────────────────────────────────────────────────────────────
        // Analysis Tab — LLM
        // ────────────────────────────────────────────────────────────

        private async void BtnSendToLlm_Click(object sender, RoutedEventArgs e)
        {
            var prompt = TxtPrompt.Text.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                MessageBox.Show("Please enter a prompt.", "Empty Prompt",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var apiUrl = TxtApiUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                MessageBox.Show("Please configure the LLM API URL.", "No API URL",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnSendToLlm.IsEnabled = false;
            BtnExportPdf.IsEnabled = false;
            SetBusy("Sending to LLM...");
            TxtLlmResponse.Text = "Waiting for response...";

            var aggregated = string.Join("\n", _contextItems.Select(i => i.RawText));
            var (response, error) = await _llmService.SendMessageAsync(apiUrl, aggregated, prompt);

            SetBusy(null);
            BtnSendToLlm.IsEnabled = true;

            if (!string.IsNullOrEmpty(error))
            {
                TxtLlmResponse.Text = $"Error: {error}";
                SetStatus($"LLM call failed: {error}", false);
            }
            else
            {
                TxtLlmResponse.Text = response;
                TxtLlmResponse.ScrollToEnd();
                BtnExportPdf.IsEnabled = true;
                SetStatus("LLM response received.", true);
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
                MessageBox.Show("No LLM response to export.", "Nothing to Export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title      = "Save Analysis Report as PDF",
                Filter     = "PDF Files (*.pdf)|*.pdf",
                FileName   = $"Analysis_{DateTime.Now:yyyyMMdd_HHmmss}.pdf",
                DefaultExt = ".pdf"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                SetBusy("Generating PDF...");
                var prompt    = TxtPrompt.Text.Trim();
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
                                row.RelativeItem()
                                    .Text("DB Analyzer — Analysis Report")
                                    .FontSize(18).Bold().FontColor("#4A3FBF");
                                row.ConstantItem(140).AlignRight()
                                    .Text(timestamp).FontSize(9).FontColor("#888888");
                            });
                            col.Item().PaddingTop(4).LineHorizontal(1).LineColor("#CCCCCC");
                        });

                        page.Content().PaddingTop(16).Column(col =>
                        {
                            if (!string.IsNullOrWhiteSpace(prompt))
                            {
                                col.Item().Text("Prompt").FontSize(13).Bold().FontColor("#333333");
                                col.Item().PaddingTop(4).PaddingBottom(14)
                                    .Background("#F5F5FA").Padding(10)
                                    .Text(prompt).FontSize(11).FontColor("#444444");
                            }
                            col.Item().Text("Analysis Result").FontSize(13).Bold().FontColor("#333333");
                            col.Item().PaddingTop(6)
                                .Text(response).FontSize(11).LineHeight(1.55f);
                        });

                        page.Footer().AlignCenter().Text(t =>
                        {
                            t.Span("Page ").FontSize(9).FontColor("#AAAAAA");
                            t.CurrentPageNumber().FontSize(9).FontColor("#AAAAAA");
                            t.Span(" / ").FontSize(9).FontColor("#AAAAAA");
                            t.TotalPages().FontSize(9).FontColor("#AAAAAA");
                        });
                    });
                }).GeneratePdf(dlg.FileName);

                SetBusy(null);
                SetStatus($"PDF saved: {dlg.FileName}", true);
                MessageBox.Show($"PDF saved successfully:\n{dlg.FileName}", "Export Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SetBusy(null);
                SetStatus($"PDF export failed: {ex.Message}", false);
                MessageBox.Show($"Failed to generate PDF:\n{ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ────────────────────────────────────────────────────────────
        // Data Sources Tab — Connection List
        // ────────────────────────────────────────────────────────────

        private void BtnAddConnection_Click(object sender, RoutedEventArgs e)
        {
            var conn = new DbConnectionInfo { Name = $"Source {_connections.Count + 1}" };
            _connections.Add(conn);
            LstConnections.SelectedItem = conn;
            SaveSettings();
        }

        private void BtnRemoveConnection_Click(object sender, RoutedEventArgs e)
        {
            if (LstConnections.SelectedItem is DbConnectionInfo conn)
            {
                var r = MessageBox.Show($"Remove \"{conn.Name}\"?", "Confirm Remove",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;

                _connections.Remove(conn);
                _editingConnection = null;
                EditPanel.IsEnabled = false;
                TxtEditHeader.Text = "Select a connection to edit";
                ClearEditForm();
                SaveSettings();
            }
        }

        private void BtnDuplicateConnection_Click(object sender, RoutedEventArgs e)
        {
            if (LstConnections.SelectedItem is not DbConnectionInfo src) return;
            var copy = new DbConnectionInfo
            {
                Name           = src.Name + " (copy)",
                Server         = src.Server,
                Database       = src.Database,
                Username       = src.Username,
                Password       = src.Password,
                UseWindowsAuth = src.UseWindowsAuth,
                SqlQuery       = src.SqlQuery
            };
            _connections.Add(copy);
            LstConnections.SelectedItem = copy;
            SaveSettings();
        }

        private void LstConnections_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstConnections.SelectedItem is DbConnectionInfo conn)
                LoadConnectionIntoForm(conn);
        }

        private void LoadConnectionIntoForm(DbConnectionInfo conn)
        {
            _editingConnection = conn;
            EditPanel.IsEnabled = true;
            TxtEditHeader.Text  = $"Editing: {conn.Name}";

            TxtEditName.Text     = conn.Name;
            TxtEditServer.Text   = conn.Server;
            TxtEditDatabase.Text = conn.Database;
            TxtEditUsername.Text = conn.Username;
            PwdEditPassword.Password = conn.Password;
            ChkWindowsAuth.IsChecked = conn.UseWindowsAuth;
            TxtEditQuery.Text    = conn.SqlQuery;

            CredentialsPanel.Visibility = conn.UseWindowsAuth ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ClearEditForm()
        {
            TxtEditName.Text     = string.Empty;
            TxtEditServer.Text   = string.Empty;
            TxtEditDatabase.Text = string.Empty;
            TxtEditUsername.Text = string.Empty;
            PwdEditPassword.Password = string.Empty;
            ChkWindowsAuth.IsChecked = false;
            TxtEditQuery.Text    = string.Empty;
        }

        private void ChkWindowsAuth_Changed(object sender, RoutedEventArgs e)
        {
            CredentialsPanel.Visibility = ChkWindowsAuth.IsChecked == true
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void BtnSaveConnection_Click(object sender, RoutedEventArgs e)
        {
            if (_editingConnection == null) return;

            if (string.IsNullOrWhiteSpace(TxtEditName.Text))
            {
                MessageBox.Show("Connection name cannot be empty.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(TxtEditServer.Text) ||
                string.IsNullOrWhiteSpace(TxtEditDatabase.Text))
            {
                MessageBox.Show("Server and Database are required.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _editingConnection.Name           = TxtEditName.Text.Trim();
            _editingConnection.Server         = TxtEditServer.Text.Trim();
            _editingConnection.Database       = TxtEditDatabase.Text.Trim();
            _editingConnection.Username       = TxtEditUsername.Text.Trim();
            _editingConnection.Password       = PwdEditPassword.Password;
            _editingConnection.UseWindowsAuth = ChkWindowsAuth.IsChecked == true;
            _editingConnection.SqlQuery       = TxtEditQuery.Text;

            // Refresh ListBox display (force re-render of item template)
            var idx = LstConnections.SelectedIndex;
            LstConnections.ItemsSource = null;
            LstConnections.ItemsSource = _connections;
            LstConnections.SelectedIndex = idx;

            TxtEditHeader.Text = $"Editing: {_editingConnection.Name}";
            SaveSettings();
            SetStatus($"Connection \"{_editingConnection.Name}\" saved.", true);
        }

        private async void BtnTestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (_editingConnection == null)
            {
                MessageBox.Show("Select a connection first.", "No Selection",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Use current form values for the test (may not be saved yet)
            var temp = new DbConnectionInfo
            {
                Server         = TxtEditServer.Text.Trim(),
                Database       = TxtEditDatabase.Text.Trim(),
                Username       = TxtEditUsername.Text.Trim(),
                Password       = PwdEditPassword.Password,
                UseWindowsAuth = ChkWindowsAuth.IsChecked == true
            };

            SetBusy($"Testing connection to {temp.Server}...");
            var (success, message) = await _databaseService.TestConnectionAsync(temp);
            SetBusy(null);

            MessageBox.Show(message,
                success ? "Connection Successful" : "Connection Failed",
                MessageBoxButton.OK,
                success ? MessageBoxImage.Information : MessageBoxImage.Error);
            SetStatus(message, success);
        }

        // ────────────────────────────────────────────────────────────
        // Helpers
        // ────────────────────────────────────────────────────────────

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
            TxtStatus.Text    = message;
            TxtStatus.Foreground = isSuccess
                ? (System.Windows.Media.Brush)FindResource("SuccessColor")
                : (System.Windows.Media.Brush)FindResource("ErrorColor");
        }
    }

    // Extension helper for JsonElement
    internal static class JsonExtensions
    {
        public static string GetStringProp(this JsonElement el, string name, string fallback = "")
            => el.TryGetProperty(name, out var p) ? (p.GetString() ?? fallback) : fallback;
    }
}

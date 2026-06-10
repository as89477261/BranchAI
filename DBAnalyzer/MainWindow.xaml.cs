using System;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DBAnalyzer.Models;
using DBAnalyzer.Services;

namespace DBAnalyzer
{
    public partial class MainWindow : Window
    {
        private readonly DatabaseService _databaseService = new DatabaseService();
        private readonly LlmService _llmService = new LlmService();
        private readonly ObservableCollection<DbConnectionInfo> _connections = new ObservableCollection<DbConnectionInfo>();
        private DataTable? _lastQueryResult;
        private string _lastQueryConnectionName = string.Empty;
        private const string SettingsFile = "appsettings.json";

        public MainWindow()
        {
            InitializeComponent();
            LstConnections.ItemsSource = _connections;
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
                    {
                        TxtApiUrl.Text = urlProp.GetString() ?? TxtApiUrl.Text;
                    }
                }
            }
            catch { /* use defaults */ }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new { LlmApiUrl = TxtApiUrl.Text.Trim() };
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
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
            var dialog = new AddConnectionDialog();
            dialog.Owner = this;
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
                TxtSelectedConnection.Text = "None selected";
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

            if (success)
            {
                MessageBox.Show(message, "Connection Test", MessageBoxButton.OK, MessageBoxImage.Information);
                SetStatus(message, isSuccess: true);
            }
            else
            {
                MessageBox.Show(message, "Connection Test Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus(message, isSuccess: false);
            }
        }

        private void LstConnections_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstConnections.SelectedItem is DbConnectionInfo conn)
            {
                TxtSelectedConnection.Text = $"{conn.Name}  ({conn.Server} / {conn.Database})";
            }
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
                MessageBox.Show("No query results to add to context. Run a query first.", "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var text = _databaseService.DataTableToText(_lastQueryResult, _lastQueryConnectionName);
            var existing = TxtAggregatedData.Text;

            if (string.IsNullOrWhiteSpace(existing))
                TxtAggregatedData.Text = text;
            else
                TxtAggregatedData.Text = existing + "\n" + text;

            TxtAggregatedData.ScrollToEnd();
            SetStatus("Results added to context.", isSuccess: true);
        }

        private void BtnClearContext_Click(object sender, RoutedEventArgs e)
        {
            TxtAggregatedData.Text = string.Empty;
            SetStatus("Context cleared.", isSuccess: true);
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
            SetBusy("Sending to LLM...");
            TxtLlmResponse.Text = "Waiting for response...";

            var aggregatedData = TxtAggregatedData.Text;

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
                SetStatus("LLM response received.", isSuccess: true);
            }
        }

        private void BtnClearResponse_Click(object sender, RoutedEventArgs e)
        {
            TxtLlmResponse.Text = string.Empty;
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

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        private readonly DbdService _dbdService = new DbdService();
        private readonly ObservableCollection<DbConnectionInfo> _connections = new ObservableCollection<DbConnectionInfo>();
        private readonly ObservableCollection<ContextItem> _contextItems = new ObservableCollection<ContextItem>();
        private const string SettingsFile = "appsettings.json";

        // Editing state
        private DbConnectionInfo? _editingConnection;

        // Queries for the currently selected connection — bound to LstQueries
        private readonly ObservableCollection<QueryItem> _currentQueries = new ObservableCollection<QueryItem>();
        private QueryItem? _editingQuery;
        private bool _suppressQueryEditorSync = false;

        public MainWindow()
        {
            InitializeComponent();
            QuestPDF.Settings.License = LicenseType.Community;
            LstConnections.ItemsSource = _connections;
            LstQueries.ItemsSource     = _currentQueries;
            ContextItemsPanel.ItemsSource = _contextItems;
            _contextItems.CollectionChanged += (_, _) => UpdateContextSummary();
            LoadSettings();
        }

        // ────────────────────────────────────────────────────────────
        // Settings
        // ────────────────────────────────────────────────────────────

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                var doc  = JsonDocument.Parse(File.ReadAllText(SettingsFile));
                var root = doc.RootElement;

                if (root.TryGetProperty("LlmApiUrl", out var url))
                    TxtApiUrl.Text = url.GetString() ?? TxtApiUrl.Text;

                if (root.TryGetProperty("Connections", out var conns))
                {
                    foreach (var c in conns.EnumerateArray())
                    {
                        var conn = new DbConnectionInfo
                        {
                            Name           = c.GetStr("Name"),
                            Server         = c.GetStr("Server"),
                            Database       = c.GetStr("Database"),
                            Username       = c.GetStr("Username"),
                            Password       = c.GetStr("Password"),
                            UseWindowsAuth = c.TryGetProperty("UseWindowsAuth", out var wa) && wa.GetBoolean(),
                        };
                        if (c.TryGetProperty("Queries", out var qs))
                        {
                            foreach (var q in qs.EnumerateArray())
                                conn.Queries.Add(new QueryItem
                                {
                                    Name = q.GetStr("Name", "Query"),
                                    Sql  = q.GetStr("Sql", "SELECT TOP 100 * FROM "),
                                });
                        }
                        _connections.Add(conn);
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
                        c.Username, c.Password, c.UseWindowsAuth,
                        Queries = c.Queries.Select(q => new { q.Name, q.Sql })
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
        // Customer ID validation
        // ────────────────────────────────────────────────────────────

        private void TxtCustomerId_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Allow only digits
            var text  = TxtCustomerId.Text;
            var clean = new string(text.Where(char.IsDigit).ToArray());
            if (clean != text)
            {
                TxtCustomerId.Text = clean;
                TxtCustomerId.CaretIndex = clean.Length;
                return;
            }

            if (clean.Length == 0)
            {
                TxtIdValidation.Text       = string.Empty;
                TxtIdValidation.Foreground = Brushes.Transparent;
            }
            else if (clean.Length < 13)
            {
                TxtIdValidation.Text       = $"ยังขาดอีก {13 - clean.Length} หลัก";
                TxtIdValidation.Foreground = (Brush)FindResource("ErrorColor");
            }
            else
            {
                TxtIdValidation.Text       = "✔  ครบ 13 หลัก";
                TxtIdValidation.Foreground = (Brush)FindResource("SuccessColor");
            }
        }

        // ────────────────────────────────────────────────────────────
        // Analysis Tab — Fetch Data
        // ────────────────────────────────────────────────────────────

        // Substitute {ID} and any other {PARAM} patterns — case-insensitive
        // Resolve {ID} placeholder — case-insensitive, supports common variants
        private string ResolveQuery(string sql, string idValue)
        {
            if (string.IsNullOrEmpty(idValue)) return sql;
            // Use regex-style replace to catch {id}, {ID}, {Id} in one pass
            return System.Text.RegularExpressions.Regex.Replace(
                sql, @"\{ID\}", idValue,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private string BuildSqlPreview()
        {
            var idValue = TxtCustomerId.Text.Trim();
            var allQueries = _connections.SelectMany(c =>
                c.Queries.Select(q => (conn: c, query: q))).ToList();

            if (allQueries.Count == 0) return "(ยังไม่มี query ใน Data Sources)";

            var sb = new StringBuilder();
            foreach (var (conn, query) in allQueries)
            {
                sb.AppendLine($"-- [{conn.Name}] {query.Name}");
                sb.AppendLine(ResolveQuery(query.Sql, idValue));
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private void BtnPreviewSql_Click(object sender, RoutedEventArgs e)
        {
            TxtSqlPreview.Text       = BuildSqlPreview();
            SqlPreviewBorder.Visibility = Visibility.Visible;
        }

        private void BtnClosePreview_Click(object sender, RoutedEventArgs e)
        {
            SqlPreviewBorder.Visibility = Visibility.Collapsed;
        }

        private async void BtnFetchData_Click(object sender, RoutedEventArgs e)
        {
            if (_connections.Count == 0)
            {
                MessageBox.Show("ยังไม่มี connection\nไปที่ Tab \"Data Sources\" เพื่อเพิ่ม",
                    "No Sources", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var allQueries = _connections.SelectMany(c =>
                c.Queries.Select(q => (conn: c, query: q))).ToList();

            if (allQueries.Count == 0)
            {
                MessageBox.Show("ยังไม่มี SQL Query\nไปที่ Tab \"Data Sources\" เพื่อเพิ่ม Query ให้แต่ละ connection",
                    "No Queries", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var idValue = TxtCustomerId.Text.Trim();

            BtnFetchData.IsEnabled = false;
            BtnPreviewSql.IsEnabled = false;
            SetBusy($"กำลังดึงข้อมูล {allQueries.Count} query จาก {_connections.Count} source...");
            _contextItems.Clear();
            TxtEmptyContext.Visibility = Visibility.Collapsed;

            int ok = 0, fail = 0;
            foreach (var (conn, query) in allQueries)
            {
                var resolvedSql = ResolveQuery(query.Sql, idValue);
                SetBusy($"กำลังรัน: [{conn.Name}] {query.Name}");

                var (data, error) = await _databaseService.ExecuteQueryAsync(conn, resolvedSql);

                if (!string.IsNullOrEmpty(error))
                {
                    // Show which resolved SQL caused the error
                    _contextItems.Add(new ContextItem
                    {
                        Header  = $"❌  {conn.Name} — {query.Name}",
                        RawText = $"[{conn.Name} / {query.Name}] Error: {error}\nSQL: {resolvedSql}\n"
                    });
                    fail++;
                }
                else if (data == null || data.Rows.Count == 0)
                {
                    _contextItems.Add(new ContextItem
                    {
                        Header  = $"⚪  {conn.Name} — {query.Name}  (0 rows)",
                        RawText = $"[{conn.Name} / {query.Name}] No rows returned.\nSQL: {resolvedSql}\n"
                    });
                    ok++;
                }
                else
                {
                    var item = _databaseService.DataTableToContextItem(data, $"{conn.Name} / {query.Name}");
                    _contextItems.Add(item);
                    ok++;
                }
            }

            SetBusy(null);
            BtnFetchData.IsEnabled  = true;
            BtnPreviewSql.IsEnabled = true;

            if (_contextItems.Count == 0)
                TxtEmptyContext.Visibility = Visibility.Visible;

            var msg = $"โหลดสำเร็จ {ok} query";
            if (fail > 0) msg += $"  |  ล้มเหลว {fail} query";
            if (!string.IsNullOrEmpty(idValue)) msg += $"  |  ID={idValue}";
            SetStatus(msg, fail == 0);
        }

        private async void BtnFetchDbd_Click(object sender, RoutedEventArgs e)
        {
            var id = TxtCustomerId.Text.Trim();
            if (id.Length != 13)
            {
                MessageBox.Show("กรุณากรอกเลข 13 หลักให้ครบก่อน", "ข้อมูลไม่ครบ",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnFetchDbd.IsEnabled = false;
            SetBusy($"กำลังดึงข้อมูลนิติบุคคลจาก DBD Open Data... ({id})");

            var (item, error) = await _dbdService.FetchJuristicAsync(id);

            SetBusy(null);
            BtnFetchDbd.IsEnabled = true;

            if (!string.IsNullOrEmpty(error))
            {
                SetStatus($"DBD fetch failed: {error}", false);
                MessageBox.Show(
                    $"ไม่สามารถดึงข้อมูลจาก DBD ได้:\n{error}\n\n" +
                    "หมายเหตุ: opendata.dbd.go.th อาจ block IP นอกไทย หรือ server ไม่พร้อม",
                    "DBD Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (item == null)
            {
                SetStatus($"ไม่พบข้อมูลนิติบุคคลเลข {id} ใน DBD Open Data", false);
                MessageBox.Show(
                    $"ไม่พบเลข {id} ใน DBD Open Data\n\n" +
                    "เป็นไปได้ว่า:\n" +
                    "• เป็นเลขบัตรประชาชน (ไม่ใช่เลขนิติบุคคล)\n" +
                    "• ยังไม่อยู่ใน dataset สาธารณะ\n" +
                    "• ลองตรวจสอบที่ datawarehouse.dbd.go.th โดยตรง",
                    "ไม่พบข้อมูล", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Add DBD result to top of context
            _contextItems.Insert(0, item);
            TxtEmptyContext.Visibility = Visibility.Collapsed;
            SetStatus($"✔ โหลดข้อมูล DBD นิติบุคคล {id} สำเร็จ", true);
        }

        private void UpdateContextSummary()
        {
            if (_contextItems.Count == 0)
            {
                TxtContextSummary.Text = "ยังไม่มีข้อมูล — กรอก ID และกด Fetch";
                TxtEmptyContext.Visibility = Visibility.Visible;
            }
            else
            {
                int total = _contextItems.Sum(i => i.Rows.Count);
                TxtContextSummary.Text = $"{_contextItems.Count} query loaded  ·  {total} total rows";
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
                MessageBox.Show("กรุณาใส่ Prompt", "Empty Prompt", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var apiUrl = TxtApiUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                MessageBox.Show("กรุณาตั้งค่า LLM API URL", "No API URL", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnSendToLlm.IsEnabled = false;
            BtnExportPdf.IsEnabled = false;
            SetBusy("กำลังส่งไป LLM...");
            TxtLlmResponse.Text = "รอผลลัพธ์...";

            var aggregated = string.Join("\n", _contextItems.Select(i => i.RawText));
            var (response, error) = await _llmService.SendMessageAsync(apiUrl, aggregated, prompt);

            SetBusy(null);
            BtnSendToLlm.IsEnabled = true;

            if (!string.IsNullOrEmpty(error))
            {
                TxtLlmResponse.Text = $"Error: {error}";
                SetStatus($"LLM failed: {error}", false);
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
            TxtLlmResponse.Text    = string.Empty;
            BtnExportPdf.IsEnabled = false;
        }

        private void BtnExportPdf_Click(object sender, RoutedEventArgs e)
        {
            var response = TxtLlmResponse.Text.Trim();
            if (string.IsNullOrWhiteSpace(response))
            {
                MessageBox.Show("ยังไม่มี LLM response", "Nothing to Export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title      = "บันทึกรายงานเป็น PDF",
                Filter     = "PDF Files (*.pdf)|*.pdf",
                FileName   = $"Analysis_{DateTime.Now:yyyyMMdd_HHmmss}.pdf",
                DefaultExt = ".pdf"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                SetBusy("กำลังสร้าง PDF...");
                var prompt    = TxtPrompt.Text.Trim();
                var customerId = TxtCustomerId.Text.Trim();
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
                            if (!string.IsNullOrEmpty(customerId))
                                col.Item().PaddingTop(4)
                                   .Text($"Customer/Patient ID: {customerId}")
                                   .FontSize(11).FontColor("#555555");
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
                MessageBox.Show($"บันทึก PDF สำเร็จ:\n{dlg.FileName}", "Export Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SetBusy(null);
                SetStatus($"PDF failed: {ex.Message}", false);
                MessageBox.Show($"ไม่สามารถสร้าง PDF:\n{ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ────────────────────────────────────────────────────────────
        // Data Sources Tab — Connection CRUD
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
            if (LstConnections.SelectedItem is not DbConnectionInfo conn) return;
            var r = MessageBox.Show($"ลบ \"{conn.Name}\" ?", "Confirm Remove",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            _connections.Remove(conn);
            _editingConnection = null;
            EditPanel.IsEnabled = false;
            TxtEditHeader.Text = "Select a connection to edit";
            ClearConnectionForm();
            SaveSettings();
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
                Queries        = src.Queries.Select(q => new QueryItem { Name = q.Name, Sql = q.Sql }).ToList()
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

            TxtEditName.Text         = conn.Name;
            TxtEditServer.Text       = conn.Server;
            TxtEditDatabase.Text     = conn.Database;
            TxtEditUsername.Text     = conn.Username;
            PwdEditPassword.Password = conn.Password;
            ChkWindowsAuth.IsChecked = conn.UseWindowsAuth;
            CredentialsPanel.Visibility = conn.UseWindowsAuth ? Visibility.Collapsed : Visibility.Visible;

            // Load queries
            _currentQueries.Clear();
            foreach (var q in conn.Queries)
                _currentQueries.Add(q);

            ClearQueryEditor();
        }

        private void ClearConnectionForm()
        {
            TxtEditName.Text         = string.Empty;
            TxtEditServer.Text       = string.Empty;
            TxtEditDatabase.Text     = string.Empty;
            TxtEditUsername.Text     = string.Empty;
            PwdEditPassword.Password = string.Empty;
            ChkWindowsAuth.IsChecked = false;
            _currentQueries.Clear();
            ClearQueryEditor();
        }

        private void ChkWindowsAuth_Changed(object sender, RoutedEventArgs e)
        {
            CredentialsPanel.Visibility = ChkWindowsAuth.IsChecked == true
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnSaveConnection_Click(object sender, RoutedEventArgs e)
        {
            if (_editingConnection == null) return;

            if (string.IsNullOrWhiteSpace(TxtEditName.Text))
            {
                MessageBox.Show("Connection name ห้ามว่าง", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(TxtEditServer.Text) ||
                string.IsNullOrWhiteSpace(TxtEditDatabase.Text))
            {
                MessageBox.Show("กรุณาระบุ Server และ Database", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _editingConnection.Name           = TxtEditName.Text.Trim();
            _editingConnection.Server         = TxtEditServer.Text.Trim();
            _editingConnection.Database       = TxtEditDatabase.Text.Trim();
            _editingConnection.Username       = TxtEditUsername.Text.Trim();
            _editingConnection.Password       = PwdEditPassword.Password;
            _editingConnection.UseWindowsAuth = ChkWindowsAuth.IsChecked == true;
            _editingConnection.Queries        = _currentQueries.ToList();

            // Refresh ListBox (force re-render)
            var idx = LstConnections.SelectedIndex;
            LstConnections.ItemsSource = null;
            LstConnections.ItemsSource = _connections;
            LstConnections.SelectedIndex = idx;

            TxtEditHeader.Text = $"Editing: {_editingConnection.Name}";
            SaveSettings();
            SetStatus($"Saved: {_editingConnection.Name}", true);
        }

        private async void BtnTestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (_editingConnection == null)
            {
                MessageBox.Show("เลือก connection ก่อน", "No Selection",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var temp = new DbConnectionInfo
            {
                Server         = TxtEditServer.Text.Trim(),
                Database       = TxtEditDatabase.Text.Trim(),
                Username       = TxtEditUsername.Text.Trim(),
                Password       = PwdEditPassword.Password,
                UseWindowsAuth = ChkWindowsAuth.IsChecked == true
            };
            SetBusy($"กำลัง test connection ไปยัง {temp.Server}...");
            var (success, message) = await _databaseService.TestConnectionAsync(temp);
            SetBusy(null);
            MessageBox.Show(message,
                success ? "Connection Successful" : "Connection Failed",
                MessageBoxButton.OK,
                success ? MessageBoxImage.Information : MessageBoxImage.Error);
            SetStatus(message, success);
        }

        // ────────────────────────────────────────────────────────────
        // Data Sources Tab — Query CRUD
        // ────────────────────────────────────────────────────────────

        private void BtnAddQuery_Click(object sender, RoutedEventArgs e)
        {
            if (_editingConnection == null) return;
            var q = new QueryItem { Name = $"Query {_currentQueries.Count + 1}" };
            _currentQueries.Add(q);
            LstQueries.SelectedItem = q;
        }

        private void BtnRemoveQuery_Click(object sender, RoutedEventArgs e)
        {
            if (LstQueries.SelectedItem is not QueryItem q) return;
            _currentQueries.Remove(q);
            if (_editingQuery == q)
            {
                _editingQuery = null;
                ClearQueryEditor();
            }
        }

        private void BtnMoveQueryUp_Click(object sender, RoutedEventArgs e)
        {
            if (LstQueries.SelectedItem is not QueryItem q) return;
            var idx = _currentQueries.IndexOf(q);
            if (idx <= 0) return;
            _currentQueries.Move(idx, idx - 1);
            LstQueries.SelectedItem = q;
        }

        private void BtnMoveQueryDown_Click(object sender, RoutedEventArgs e)
        {
            if (LstQueries.SelectedItem is not QueryItem q) return;
            var idx = _currentQueries.IndexOf(q);
            if (idx < 0 || idx >= _currentQueries.Count - 1) return;
            _currentQueries.Move(idx, idx + 1);
            LstQueries.SelectedItem = q;
        }

        private void LstQueries_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstQueries.SelectedItem is QueryItem q)
                LoadQueryIntoEditor(q);
            else
                ClearQueryEditor();
        }

        private void LoadQueryIntoEditor(QueryItem q)
        {
            _editingQuery = q;
            _suppressQueryEditorSync = true;
            TxtEditQueryName.Text = q.Name;
            TxtEditQuerySql.Text  = q.Sql;
            TxtEditQuerySql.IsEnabled = true;
            QueryEditorPanel.Visibility = Visibility.Visible;
            TxtQueryEditorLabel.Text = "EDITING QUERY";
            _suppressQueryEditorSync = false;
        }

        private void ClearQueryEditor()
        {
            _editingQuery = null;
            _suppressQueryEditorSync = true;
            TxtEditQueryName.Text = string.Empty;
            TxtEditQuerySql.Text  = string.Empty;
            TxtEditQuerySql.IsEnabled = false;
            QueryEditorPanel.Visibility = Visibility.Collapsed;
            TxtQueryEditorLabel.Text = "SELECT A QUERY ABOVE TO EDIT";
            _suppressQueryEditorSync = false;
        }

        // Live-sync editor → model (no explicit save needed for queries)
        private void TxtEditQueryName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressQueryEditorSync || _editingQuery == null) return;
            _editingQuery.Name = TxtEditQueryName.Text;
            // Refresh ListBox item display
            var idx = LstQueries.SelectedIndex;
            LstQueries.ItemsSource = null;
            LstQueries.ItemsSource = _currentQueries;
            LstQueries.SelectedIndex = idx;
        }

        private void TxtEditQuerySql_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressQueryEditorSync || _editingQuery == null) return;
            _editingQuery.Sql = TxtEditQuerySql.Text;
        }

        // ────────────────────────────────────────────────────────────
        // Helpers
        // ────────────────────────────────────────────────────────────

        private void SetBusy(string? message)
        {
            TxtStatusBar.Text = message ?? "Ready";
            LoadingProgress.Visibility = message != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetStatus(string message, bool isSuccess)
        {
            TxtStatusBar.Text = message;
            TxtStatus.Text    = message;
            TxtStatus.Foreground = isSuccess
                ? (Brush)FindResource("SuccessColor")
                : (Brush)FindResource("ErrorColor");
        }
    }

    internal static class JsonExt
    {
        public static string GetStr(this JsonElement el, string name, string fallback = "")
            => el.TryGetProperty(name, out var p) ? (p.GetString() ?? fallback) : fallback;
    }
}

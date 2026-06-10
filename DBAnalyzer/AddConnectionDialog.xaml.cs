using System.Windows;
using System.Windows.Media;
using DBAnalyzer.Models;
using DBAnalyzer.Services;

namespace DBAnalyzer
{
    public partial class AddConnectionDialog : Window
    {
        private readonly DatabaseService _databaseService = new DatabaseService();
        public DbConnectionInfo? Result { get; private set; }

        public AddConnectionDialog()
        {
            InitializeComponent();
        }

        private void ChkWindowsAuth_Changed(object sender, RoutedEventArgs e)
        {
            bool useWindows = ChkWindowsAuth.IsChecked == true;
            PnlCredentials.IsEnabled = !useWindows;
            PnlPassword.IsEnabled = !useWindows;
            PnlCredentials.Opacity = useWindows ? 0.4 : 1.0;
            PnlPassword.Opacity = useWindows ? 0.4 : 1.0;
        }

        private DbConnectionInfo BuildConnectionFromForm()
        {
            return new DbConnectionInfo
            {
                Name = TxtName.Text.Trim(),
                Server = TxtServer.Text.Trim(),
                Database = TxtDatabase.Text.Trim(),
                Username = TxtUsername.Text.Trim(),
                Password = TxtPassword.Password,
                UseWindowsAuth = ChkWindowsAuth.IsChecked == true
            };
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            var conn = BuildConnectionFromForm();

            if (string.IsNullOrWhiteSpace(conn.Server) || string.IsNullOrWhiteSpace(conn.Database))
            {
                TxtTestResult.Text = "Server and Database are required.";
                TxtTestResult.Foreground = (Brush)FindResource("ErrorColor");
                return;
            }

            TxtTestResult.Text = "Testing...";
            TxtTestResult.Foreground = (Brush)FindResource("TextSecondary");

            var (success, message) = await _databaseService.TestConnectionAsync(conn);

            TxtTestResult.Text = success ? "Connected!" : "Failed";
            TxtTestResult.Foreground = success
                ? (Brush)FindResource("SuccessColor")
                : (Brush)FindResource("ErrorColor");

            if (!success)
            {
                MessageBox.Show(message, "Connection Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            var conn = BuildConnectionFromForm();

            if (string.IsNullOrWhiteSpace(conn.Name))
            {
                MessageBox.Show("Please enter a connection name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtName.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(conn.Server))
            {
                MessageBox.Show("Please enter the server address.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtServer.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(conn.Database))
            {
                MessageBox.Show("Please enter the database name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtDatabase.Focus();
                return;
            }

            if (!conn.UseWindowsAuth && string.IsNullOrWhiteSpace(conn.Username))
            {
                MessageBox.Show("Please enter a username or use Windows Authentication.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtUsername.Focus();
                return;
            }

            Result = conn;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

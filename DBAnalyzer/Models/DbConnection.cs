using System;

namespace DBAnalyzer.Models
{
    public class DbConnectionInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Server { get; set; } = string.Empty;
        public string Database { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public bool UseWindowsAuth { get; set; } = false;
        public string SqlQuery { get; set; } = "SELECT TOP 100 * FROM ";

        public string BuildConnectionString()
        {
            if (UseWindowsAuth)
                return $"Server={Server};Database={Database};Integrated Security=True;TrustServerCertificate=True;";
            else
                return $"Server={Server};Database={Database};User Id={Username};Password={Password};TrustServerCertificate=True;";
        }

        public override string ToString() => $"{Name}  |  {Server}  |  {Database}";
    }
}

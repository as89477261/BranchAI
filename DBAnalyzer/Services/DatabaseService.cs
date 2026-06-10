using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using DBAnalyzer.Models;

namespace DBAnalyzer.Services
{
    public class DatabaseService
    {
        public async Task<(DataTable? data, string error)> ExecuteQueryAsync(DbConnectionInfo connectionInfo, string query)
        {
            try
            {
                var connectionString = connectionInfo.BuildConnectionString();
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand(query, connection);
                command.CommandTimeout = 30;

                using var adapter = new SqlDataAdapter(command);
                var dataTable = new DataTable();
                await Task.Run(() => adapter.Fill(dataTable));

                return (dataTable, string.Empty);
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }

        public async Task<(bool success, string message)> TestConnectionAsync(DbConnectionInfo connectionInfo)
        {
            try
            {
                var connectionString = connectionInfo.BuildConnectionString();
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                return (true, "Connection successful!");
            }
            catch (Exception ex)
            {
                return (false, $"Connection failed: {ex.Message}");
            }
        }

        public string DataTableToText(DataTable dataTable, string queryName = "")
        {
            if (dataTable == null || dataTable.Rows.Count == 0)
                return string.IsNullOrEmpty(queryName) ? "No results returned." : $"[{queryName}] No results returned.";

            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(queryName))
                sb.AppendLine($"=== {queryName} ===");

            // Calculate column widths
            var colWidths = new int[dataTable.Columns.Count];
            for (int i = 0; i < dataTable.Columns.Count; i++)
            {
                colWidths[i] = dataTable.Columns[i].ColumnName.Length;
            }

            foreach (DataRow row in dataTable.Rows)
            {
                for (int i = 0; i < dataTable.Columns.Count; i++)
                {
                    var val = row[i]?.ToString() ?? "NULL";
                    if (val.Length > colWidths[i])
                        colWidths[i] = Math.Min(val.Length, 50);
                }
            }

            // Header
            var header = new StringBuilder("|");
            var separator = new StringBuilder("+");
            for (int i = 0; i < dataTable.Columns.Count; i++)
            {
                var colName = dataTable.Columns[i].ColumnName.PadRight(colWidths[i]);
                header.Append($" {colName} |");
                separator.Append(new string('-', colWidths[i] + 2) + "+");
            }

            sb.AppendLine(separator.ToString());
            sb.AppendLine(header.ToString());
            sb.AppendLine(separator.ToString());

            // Rows
            foreach (DataRow row in dataTable.Rows)
            {
                var rowSb = new StringBuilder("|");
                for (int i = 0; i < dataTable.Columns.Count; i++)
                {
                    var val = (row[i]?.ToString() ?? "NULL");
                    if (val.Length > 50) val = val.Substring(0, 47) + "...";
                    val = val.PadRight(colWidths[i]);
                    rowSb.Append($" {val} |");
                }
                sb.AppendLine(rowSb.ToString());
            }

            sb.AppendLine(separator.ToString());
            sb.AppendLine($"({dataTable.Rows.Count} rows)");
            sb.AppendLine();

            return sb.ToString();
        }
    }
}

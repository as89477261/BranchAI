using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using DBAnalyzer.Models;
using System.Linq;

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

        public ContextItem DataTableToContextItem(DataTable dataTable, string queryName = "")
        {
            var item = new ContextItem
            {
                Header = $"{queryName}  —  {dataTable.Rows.Count} row(s)"
            };

            var columns = dataTable.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();

            foreach (DataRow row in dataTable.Rows)
            {
                var contextRow = new ContextRow();
                foreach (var col in columns)
                {
                    var val = row[col]?.ToString() ?? "NULL";
                    contextRow.Fields.Add(new ContextField { Key = col, Value = val });
                }
                item.Rows.Add(contextRow);
            }

            // Build plain text version for LLM context
            var sb = new StringBuilder();
            sb.AppendLine($"[{queryName}]  ({dataTable.Rows.Count} rows)");
            foreach (DataRow row in dataTable.Rows)
            {
                foreach (var col in columns)
                    sb.AppendLine($"  {col}: {row[col]?.ToString() ?? "NULL"}");
                sb.AppendLine();
            }
            item.RawText = sb.ToString();

            return item;
        }

        public string DataTableToText(DataTable dataTable, string queryName = "")
            => DataTableToContextItem(dataTable, queryName).RawText;
    }
}

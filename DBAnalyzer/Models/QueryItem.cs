namespace DBAnalyzer.Models
{
    public class QueryItem
    {
        public string Name { get; set; } = "Query";
        public string Sql  { get; set; } = "SELECT TOP 100 * FROM ";
    }
}

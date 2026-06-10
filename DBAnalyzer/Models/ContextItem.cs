using System.Collections.Generic;

namespace DBAnalyzer.Models
{
    public class ContextField
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class ContextRow
    {
        public List<ContextField> Fields { get; set; } = new();
    }

    public class ContextItem
    {
        public string Header { get; set; } = string.Empty;
        public List<ContextRow> Rows { get; set; } = new();
        public string RawText { get; set; } = string.Empty;
    }
}

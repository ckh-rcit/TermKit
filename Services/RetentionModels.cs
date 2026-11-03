namespace TermKit.Services
{
    public sealed class RetentionConversionRequest
    {
        public string TaskNumber { get; set; } = string.Empty;
        public string TargetUserInput { get; set; } = string.Empty;
        public string DelegateInput { get; set; } = string.Empty;
        // The termination does not need the mailbox retained; the date is still stamped for the cleanup job and the delegate is optional.
        public bool NoRetention { get; set; }
        public string RetentionDateText { get; set; } = string.Empty;
    }

    public sealed class RetentionConversionResult
    {
        public bool Success { get; set; }
        public string SummaryText { get; set; } = string.Empty;
    }
}

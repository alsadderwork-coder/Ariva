namespace Ariva.Infra.Services.Reports;

/// <summary>
/// ARV-294 (CWE-400): the most a scheduled report's send may take in all, its connection, authentication and message together. The
/// send runs inside the delivery's transaction, which holds the delivery's row and the deliveries' report database slot (ARV-234), and
/// MailKit bounds each network operation by <c>Email:Smtp:TimeoutSeconds</c>, not a whole send (about 15 of them, so up to 75 minutes
/// at the setting's 300 s). Past this budget the send is ended where it is and the delivery counted as failed, so its transaction never
/// waits on a slow relay longer than this, well below the runtime login's idle transaction limit (at least 300 s). Fixed, so no setting
/// can lift it; registered by tests that want it shorter.
/// </summary>
public sealed record ReportSendBudget(TimeSpan Total)
{
    #region Constants

    /// <summary>Three minutes: a report of a few megabytes at a hundred kilobytes a second, and room to spare.</summary>
    public const int DefaultSeconds = 180;

    /// <summary>The budget the hosts run with.</summary>
    public static readonly ReportSendBudget Default = new(TimeSpan.FromSeconds(DefaultSeconds));

    #endregion
}

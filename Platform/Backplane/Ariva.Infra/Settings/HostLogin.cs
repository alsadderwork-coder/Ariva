namespace Ariva.Infra.Settings;

/// <summary>One host's login under Database:HostLogins (ARV-083a): its section, its role (script 0074) and its name and password.</summary>
internal sealed record HostLogin(string Section, string Role, DatabaseLoginSettings Login);

namespace Fixtures.Bad;

public class BadCode
{
    public async Task Run(Request request, string host, string id, Client client, string clientSecret, Totp totp, string code)
    {
        Process.Start("tool");
        var shell = new[] { "cmd.exe", "x" };
        await CSharpScript.EvaluateAsync(request.Code);
        var t = Type.GetType(request.TypeName);
        var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };
        var template = Handlebars.Compile(templateFromDatabase);
        var http = new HttpClient();
        var url = $"https://{host}/flights";
        var rows = await storage.ExecuteSqlAsync<Row>($"select * from flight where id = '{id}'");
        command.CommandText = $"delete from zone where id = {id}";
        options.TokenValidationParameters = new() { ValidateLifetime = false };
        if (client.ClientSecret != clientSecret) return;
        var ok = totp.VerifyTotp(code, out _, VerificationWindow.RfcSpecifiedNetworkDelay);
        if (User.IsInRole("admin")) { }
        var claim = new Claim(ClaimTypes.Role, request.Role);
        HttpContext.Session.SetString("site", request.Site);
        var token = Request.Query["token"];
        cookie.SecurePolicy = CookieSecurePolicy.None;
        unsafe { }
        kestrel.Limits.MaxRequestBodySize = null;
        var html = Html.Raw(request.Body);
    }

    [AllowAnonymous]
    public void Open() { }

    [DllImport("native.dll")]
    static extern void Native();
}

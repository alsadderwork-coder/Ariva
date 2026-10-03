// Fixture: a handler built in Backplane outside the outbound transport (SEC-023).
public static class BadHandler
{
    public static object Create() => new SocketsHttpHandler { AllowAutoRedirect = true };
}

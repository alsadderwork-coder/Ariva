namespace Fixtures.Bad;

public static class BadProgram
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/zones", () => "all zones");
    }
}

public class LiveHub : Hub
{
}

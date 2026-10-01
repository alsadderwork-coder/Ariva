namespace Fixtures.Good;

public static class GoodProgram
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/zones", () => "zones")
           .RequireAuthorization("ViewZone");
    }
}

[Authorize]
public class LiveHub : Hub
{
}

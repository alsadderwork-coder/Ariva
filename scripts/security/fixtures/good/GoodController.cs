namespace Fixtures.Good;

[ApiController, Route("AdminArea/Device")]
[Permission(nameof(Permissions.ViewDevice))]
public class Controller(ISvcDevice svc, ISiteScope siteScope) : ControllerBase
{
    [HttpGet("{siteId}")]
    public async Task<Result<List<DeviceViewModel>>> List(Guid siteId)
        => await siteScope.Ensure(siteId) is { HasErrors: true } denied ? denied.As<List<DeviceViewModel>>() : await svc.List(siteId);

    [HttpPost]
    [Permission(nameof(Permissions.CreateDevice))]
    public Task<Result<Guid>> Create([FromBody] CreateDeviceRequest request) => svc.Create(request);
}

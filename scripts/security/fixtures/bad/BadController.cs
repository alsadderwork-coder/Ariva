namespace Fixtures.Bad;

[ApiController, Route("AdminArea/Device")]
public class Controller(ISvcDevice svc) : ControllerBase
{
    [HttpGet("{siteId}")]
    public Task<Result<List<DeviceViewModel>>> List(Guid siteId) => svc.List(siteId);

    [HttpPost]
    [Permission(nameof(Permissions.CreateDevice))]
    public Task<Result<Guid>> Create([FromBody] Device device) => svc.Create(device);
}

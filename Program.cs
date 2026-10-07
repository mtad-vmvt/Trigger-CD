using App;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(true);
var app = builder.Build();

var cfg = new Config(app);
Directory.CreateDirectory("data/files");
DeploymentEndpoints.Map(app, cfg, new DeploymentRunner());

app.Run();

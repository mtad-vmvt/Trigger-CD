using App;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(true);
var app = builder.Build();

var cfg = new Config(app);
Directory.CreateDirectory("data/files");
var deployments = new DeploymentManager(new DeploymentRunner(), app.Lifetime.ApplicationStopping);
DeploymentEndpoints.Map(app, cfg, deployments);

app.Run();

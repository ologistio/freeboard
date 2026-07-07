using ConsoleAppFramework;
using Freeboard.CLI;

var app = ConsoleApp.Create();
app.Add<GitOpsCommands>("gitops");
app.Add<SystemCommands>("system");
app.Add<UserCommands>("user");
app.Add<VendorCommands>("vendor");
app.Add<CollectorCommands>("collector");
app.Run(args);
return Environment.ExitCode;

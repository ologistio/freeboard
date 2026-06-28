using ConsoleAppFramework;
using Freeboard.CLI;

var app = ConsoleApp.Create();
app.Add<GitOpsCommands>("gitops");
app.Add<SystemCommands>("system");
app.Add<UserCommands>("user");
app.Run(args);
return Environment.ExitCode;

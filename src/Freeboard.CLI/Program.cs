using ConsoleAppFramework;
using Freeboard.CLI;

var app = ConsoleApp.Create();
app.Add<GitOpsCommands>("gitops");
app.Add<SystemCommands>("system");
app.Run(args);
return Environment.ExitCode;

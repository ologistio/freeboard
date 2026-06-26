using ConsoleAppFramework;
using Freeboard.CLI;

var app = ConsoleApp.Create();
app.Add<GitOpsCommands>("gitops");
app.Run(args);
return Environment.ExitCode;

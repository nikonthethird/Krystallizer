/// Module containing the entry point of the program.
[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module NikonTheThird.Krystallizer.Program


open FSharp.Control
open NikonTheThird.Krystallizer.Configuration
open NikonTheThird.Krystallizer.Database
open NikonTheThird.Krystallizer.DuplicateHandling
open NikonTheThird.Krystallizer.OutputGeneration
open NikonTheThird.Krystallizer.ProfileHandling
open Serilog
open Serilog.Events
open System
open System.IO
open System.Threading


/// Sets up the Serilog logging framework.
let private configureLogging () =
    let logFileName = sprintf "Log-%s.txt" (programStartDateTime.ToString "yyyy-MM-dd")
    let logDirectoryPath = Path.Combine (executingAssemblyDirectoryInfo.FullName, "Logs")
    Directory.CreateDirectory logDirectoryPath |> ignore
    let loggerConfiguration = LoggerConfiguration ()
    loggerConfiguration.WriteTo.Console (restrictedToMinimumLevel = LogEventLevel.Information) |> ignore
    loggerConfiguration.WriteTo.File (Path.Combine (logDirectoryPath, logFileName), restrictedToMinimumLevel = LogEventLevel.Debug) |> ignore
    Log.Logger <- loggerConfiguration.CreateLogger ()


/// The main asynchronous task of the program.
/// It reads the profile to execute from the configuration and
/// starts the profile handling and duplicate processing.
let private mainTask arguments = async {
    do configureLogging ()
    do Log.Information "Program launched"
    let! configuration =
        Path.Combine (executingAssemblyDirectoryInfo.FullName, "Configuration.json")
        |> readConfigurationFile
    let profile = readProfileFromArguments configuration arguments
    use! database = DatabaseConnection.EstablishConnection configuration.ConnectionString
    do! database.CreateDatabaseStructure ()
    do! handleProfile configuration database profile
    let! rootDirectoryModels =
        buildModelsForRootDirectoryNodes configuration database
        |> AsyncSeq.toListAsync
    do! generateDuplicatesFile configuration rootDirectoryModels
    do Log.Information "Program stopped"
    return 0
}


/// Returns a cancellation token source that will be cancelled when
/// the user hits Ctrl+C on the terminal.
let createCancellationTokenSource () =
    let cts = new CancellationTokenSource ()
    Console.CancelKeyPress
    |> Event.add (fun e ->
        Log.Information "Cancelling the operation"
        e.Cancel <- true
        cts.Cancel ()
    )
    cts


/// Entry point of the program.
/// Starts the main asynchronous task and prints errors that occur.
[<EntryPoint>]
let main arguments =
    let cts = createCancellationTokenSource ()
    try Async.RunSynchronously (mainTask arguments, cancellationToken = cts.Token)
    with
    | MissingConfigurationFileException path ->
        printfn "Place JSON configuration file at %s." path
        2
    | UnknownProfileException profileName ->
        printfn "Unknown profile %s" profileName
        3
    | :? OperationCanceledException ->
        Log.Information "Program cancelled"
        0
    | ex ->
        Log.Fatal (ex, "Unhandled exception encountered")
        printfn "Program crashed, check log"
        1

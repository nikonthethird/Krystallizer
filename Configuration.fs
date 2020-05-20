/// Module containing all the configuration options of the program.
[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module NikonTheThird.Krystallizer.Configuration


open Serilog
open System.IO
open System.Text.Json


/// Exception raised when the configuration file could not be
/// found. Contains the path where it was expected to be.
exception MissingConfigurationFileException of path : string


/// Exception raised when a profile name is passed as a
/// command line argument that does not exist.
exception UnknownProfileException of profile : string


/// Specifies an execution profile of the program.
type [<Struct>] Profile = {
    /// Names all the root directories that should be handled as
    /// part of this profile.
    RootDirectories : string list
    /// Specifies whether this profile performs trash file removal.
    RemoveTrash : bool
    /// Specifies whether this profile removes completely empty files.
    RemoveEmptyFiles : bool
    /// Specifies whether a file sizes are checked to see if a hash
    /// needs to be recomputed. The file size lookup can be quite
    /// an expensive operation.
    CheckFileSizes : bool
}


/// Specifies the global configuration of the program.
type [<Struct>] Configuration = {
    /// The connection string used to connect to the database.
    ConnectionString : string
    /// The path relative to the executing assembly where the
    /// root directories reside.
    RootDirectoryParentPath : string
    /// The path relative to the executing assembly where the
    /// file containing the duplicates should be generated.
    DuplicatesFilePath : string
    /// The regular expression used to detect trash files.
    TrashRegex : string
    /// The number of parallel file hash operations.
    DegreeOfParallelism : int32
    /// The execution profiles of the program that determine
    /// which root directories to handle.
    Profiles : Map<string, Profile>
}


/// Reads and parses the configuration file at the given path.
/// If it does not exist, a MissingConfigurationFileException is raised.
let readConfigurationFile path = async {
    let! token = Async.CancellationToken
    try use fileStream = File.OpenRead path
        return! JsonSerializer.DeserializeAsync<Configuration> (fileStream, jsonSerializerOptions, token)
        |> Async.AwaitValueTask
    with :? FileNotFoundException ->
        do Log.Error ("Could not find configuration file at {path}", path)
        return raise (MissingConfigurationFileException path)
}


/// Attempts to read an execution profile from the given command line
/// arguments. The default execution profile is used when none is specified.
/// If the profile does not exist, a UnknownProfileException is raised.
let readProfileFromArguments configuration =
    Array.tryHead
    >> Option.defaultValue "default"
    >> fun profileName ->
        configuration.Profiles
        |> Map.tryFind profileName
        |> Option.defaultWith (fun () -> raise (UnknownProfileException profileName))

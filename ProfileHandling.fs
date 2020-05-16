/// Module containing functions to execute a profile to
/// remove trash files from directories (when permitted by the profile),
/// clean up stale file and directory entries from the database and
/// to introduce new file and directory entries into the database.
[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module NikonTheThird.Krystallizer.ProfileHandling


open FSharp.Control
open NikonTheThird.Krystallizer.Configuration
open NikonTheThird.Krystallizer.Database
open System.IO
open System.Security.Cryptography
open System.Text.RegularExpressions


/// Logger for this module.
let rec private logger = getModuleLogger <@ logger @>


/// Contains all information required for handling a specific DirectoryInfo.
type private DirectoryHandlingState private (configuration, database, profile, trashRegex, rootDirectoryInfo, directoryInfo, directoryEntry) =
    /// DirectoryInfos of the handled DirectoryInfo that are only computed when required.
    let subdirectoryInfos = lazy (
        (directoryInfo : DirectoryInfo).EnumerateDirectories ()
        |> Seq.sortBy (fun directoryInfo -> directoryInfo.Name.ToLowerInvariant ())
        |> Seq.cache
    )
    
    /// FileInfos of the handled DirectoryInfo that are only computed when required.
    let fileInfos = lazy (
        directoryInfo.EnumerateFiles ()
        |> Seq.sortBy (fun fileInfo -> fileInfo.Name.ToLowerInvariant ())
        |> Seq.cache
    )

    /// Set of all the subdirectory names of the handled DirectoryInfo that is
    /// only computed when required.
    let subdirectoryInfoNameSet = lazy (
        subdirectoryInfos.Value
        |> Seq.map (fun directoryInfo -> directoryInfo.Name)
        |> Set.ofSeq
    )

    /// Set of all the file names of the handled DirectoryInfo that is
    /// only computed when required.
    let fileInfoNameSet = lazy (
        fileInfos.Value
        |> Seq.map (fun fileInfo -> fileInfo.Name)
        |> Set.ofSeq 
    )

    /// Cache for subdirectory entries of the handled DirectoryEntry that
    /// is filled when they are fetched from the database.
    let mutable subdirectoryEntryCache = ValueNone

    /// Cache for file entries of the handled DirectoryEntry that is
    /// filled when they are fetched from the database.
    let mutable fileEntryCache = ValueNone

    /// Cache for a map of file names to file entries of the handled
    /// DirectoryEntry that is filled when required.
    let mutable fileEntryNameCache = ValueNone

    /// Creates a handler for the given directory info and directory entry.
    new (configuration, database, profile, rootDirectoryInfo, directoryInfo, directoryEntry) =
        let trashRegex = Regex configuration.TrashRegex
        DirectoryHandlingState (configuration, database, profile, trashRegex, rootDirectoryInfo, directoryInfo, directoryEntry)

    /// Returns the global program configuration.
    member _.Configuration : Configuration = configuration

    /// Returns the database connection. 
    member _.Database : DatabaseConnection = database

    /// Returns the currently executing profile.
    member _.Profile : Profile = profile

    /// Returns the regular expression for removing trash files.
    member _.TrashRegex : Regex = trashRegex

    /// The directory info of the directory currently being handled.
    member _.DirectoryInfo = directoryInfo

    /// The database directory entry of the directory currently being handled.
    member _.DirectoryEntry : DirectoryEntry = directoryEntry

    /// A lazily computed sequence of all subdirectory infos of the directory
    /// currently being handled.
    member _.SubdirectoryInfos = subdirectoryInfos.Value

    /// A lazily computed sequence of all file infos of the directory
    /// currently being handled.
    member _.FileInfos = fileInfos.Value

    /// A lazily computed set of all the subdirectory names of the directory
    /// currently being handled.
    member _.SubdirectoryInfoNameSet = subdirectoryInfoNameSet.Value

    /// A lazily computed set of all the file names of the directory
    /// currently being handled.
    member _.FileInfoNameSet = fileInfoNameSet.Value

    /// Creates a handler for the given directory info and directory entry
    /// based on the current configuration.
    member _.CreateSubdirectoryState (directoryInfo', directoryEntry') =
        DirectoryHandlingState (configuration, database, profile, trashRegex, rootDirectoryInfo, directoryInfo', directoryEntry')

    /// Trims the path of the root directory parent directory from the given path,
    /// which means that the returned path starts at the root directory.
    member _.RemoveRootParentPathFrom (path : string) =
        (rootDirectoryInfo : DirectoryInfo).Parent.FullName.Length
        |> path.Substring
        |> fun path -> path.TrimStart ('\\', '/')

    /// A list of all subdirectory entries of the directory currently
    /// being handled. The list is only fetched once from the database
    /// and then cached.
    member _.SubdirectoryEntries = async {
        match subdirectoryEntryCache with
        | ValueSome subdirectoryEntries ->
            return subdirectoryEntries
        | ValueNone ->
            let! subdirectoryEntries =
                ValueSome directoryEntry.Id
                |> database.GetDirectoriesByParentId 
                |> AsyncSeq.toListAsync
            do subdirectoryEntryCache <- ValueSome subdirectoryEntries
            return subdirectoryEntries
    }

    /// A list of all file entries of the directory currently being
    /// handled. The list is only fetched once from the database and
    /// then cached.
    member _.FileEntries = async {
        match fileEntryCache with
        | ValueSome fileEntries ->
            return fileEntries
        | ValueNone ->
            let! fileEntries =
                directoryEntry.Id
                |> database.GetFilesByDirectoryId 
                |> AsyncSeq.toListAsync
            do fileEntryCache <- ValueSome fileEntries
            return fileEntries
    }

    /// A map of all file names to their corresponding file entries of
    /// the directory currently being handled. The map is only fetched
    /// once from the database (if the FileEntries have not been accessed)
    /// and then cached.
    member this.FileEntryNameMap = async {
        match fileEntryNameCache with
        | ValueSome fileEntryNameMap ->
            return fileEntryNameMap
        | ValueNone ->
            let! fileEntries = this.FileEntries
            let fileEntryNameMap =
                fileEntries
                |> Seq.map (fun ({ Name = name } as fileEntry) -> name, fileEntry)
                |> Map.ofSeq
            do fileEntryNameCache <- ValueSome fileEntryNameMap
            return fileEntryNameMap
    }


/// Computes the SHA1 hash of the given file info and returns it.
let private computeHash (fileInfo : FileInfo) = async {
    use fileStream = fileInfo.OpenRead ()
    use algorithm = SHA1.Create ()    
    return! algorithm.ComputeHashAsync fileStream |> Async.AwaitTask
}


/// Returns the directory info of the given root directory name.
let private getRootDirectoryInfo configuration rootDirectoryName =
    Path.Combine (
        executingAssemblyDirectoryInfo.FullName,
        configuration.RootDirectoryParentPath,
        rootDirectoryName
    )
    |> DirectoryInfo


/// Fetches the directory entry with the given parent id and name from the
/// database or creates it if it doesn't exist.
let private getOrAddDirectoryEntry (database : DatabaseConnection) parentId name = async {
    match! database.TryGetDirectoryByParentIdAndName (parentId, name) with
    | ValueSome directoryEntry ->
        return directoryEntry
    | ValueNone ->
        return! database.AddDirectory {
            Id       = 0
            ParentId = parentId
            Name     = name
        }
}


/// Checks if any files in the currently handled directory match the trash
/// regex, and if they do, deletes them.
let private removeTrashFromDirectory (state : DirectoryHandlingState) = async {
    do logger.Debug (
        "Removing trash from directory {path}",
        state.RemoveRootParentPathFrom state.DirectoryInfo.FullName
    )
    for fileInfo in state.FileInfos do
        if state.TrashRegex.IsMatch fileInfo.Name then
            do logger.Information (
                "Removing trash file at {path}",
                state.RemoveRootParentPathFrom fileInfo.FullName
            )
            do fileInfo.Delete ()
}


/// Checks if there are file entries in the database for the currently handled
/// directory that no longer exist on disk. Removes them.
let private cleanupDatabaseFileEntries (state : DirectoryHandlingState) = async {
    do logger.Debug (
        "Cleaning up database file entries of directory {path}",
        state.RemoveRootParentPathFrom state.DirectoryInfo.FullName
    )
    for { Id = id; Name = name } in state.FileEntries do
        if state.FileInfoNameSet |> Set.contains name |> not then
            do logger.Information (
                "Removing file entry {name} from directory {path} (id {directoryId})",
                name,
                state.RemoveRootParentPathFrom state.DirectoryInfo.FullName,
                state.DirectoryEntry.Id
            )
            do! state.Database.RemoveFile id
}


/// Checks if there are directory entries in the database for the currently
/// handled directory that no longer exist on disk. Removes them, delete
/// cascade will take care of any subdirectories and files.
let private cleanupDatabaseDirectoryEntries (state : DirectoryHandlingState) = async {
    do logger.Debug (
        "Cleaning up database directory entries of directory {path}",
        state.RemoveRootParentPathFrom state.DirectoryInfo.FullName
    )
    for { Id = id; Name = name } in state.SubdirectoryEntries do
        if state.SubdirectoryInfoNameSet |> Set.contains name |> not then
            do logger.Information (
                "Removing subdirectory entry {name} from directory {path} (id {directoryId})",
                name,
                state.RemoveRootParentPathFrom state.DirectoryInfo.FullName,
                state.DirectoryEntry.Id
            )
            do! state.Database.RemoveDirectory id
}


/// Checks if there are file infos in the currently handled directory
/// that have no entries in the database. Hashes them.
let private getFileInfosToHash (state : DirectoryHandlingState) = asyncSeq {
    do logger.Debug (
        "Getting file infos to hash in directory {path}",
        state.RemoveRootParentPathFrom state.DirectoryInfo.FullName
    )
    for fileInfo in state.FileInfos do
        match! state.FileEntryNameMap |> Async.Map (Map.tryFind fileInfo.Name) with
        | Some { Length = length } when length = fileInfo.Length ->
            // The file in its current state is stored.
            do ()
        | Some { Id = id } ->
            // The file is stored, but it is no longer current.
            // Get rid of the old entry and rehash it.
            do! state.Database.RemoveFile id
            yield struct (state, fileInfo)
        | None ->
            // The file has not been stored yet.
            yield struct (state, fileInfo)
}


/// Handles the directory represented by the given state and returns all file
/// infos that require hashing.
/// If the executing profile has trash removal enabled, first trash files will
/// be checked for and removed. Then stale file and subdirectory entries will
/// be removed from the database, then returns all files of the current
/// directory that need to be hashed and finally returns all files that need
/// to be hashed of the subdirectories as well.
let rec private handleDirectory (state : DirectoryHandlingState) = asyncSeq {
    do logger.Information (
        "Handling directory {path}",
        state.RemoveRootParentPathFrom state.DirectoryInfo.FullName
    )
    if state.Profile.RemoveTrash then
        do! removeTrashFromDirectory state
    do! cleanupDatabaseFileEntries state
    do! cleanupDatabaseDirectoryEntries state
    yield! getFileInfosToHash state
    for subdirectoryInfo in state.SubdirectoryInfos do
        let! subdirectoryEntry = getOrAddDirectoryEntry state.Database (ValueSome state.DirectoryEntry.Id) subdirectoryInfo.Name
        yield! state.CreateSubdirectoryState (subdirectoryInfo, subdirectoryEntry)
        |> handleDirectory
}


/// Handles all root directories in the given profile. Performs trash removal
/// if enabled and stale database entry cleanup and returns all files from all
/// subdirectories that require hashing into the database.
let private handleRootDirectories configuration database profile = asyncSeq {
    do logger.Information "Handling all root directories of the profile"
    for rootDirectoryName in profile.RootDirectories do
        let rootDirectoryInfo = getRootDirectoryInfo configuration rootDirectoryName
        if rootDirectoryInfo.Exists then
            let! rootDirectoryEntry = getOrAddDirectoryEntry database ValueNone rootDirectoryInfo.Name
            yield! DirectoryHandlingState (
                configuration,
                database,
                profile,
                rootDirectoryInfo,
                rootDirectoryInfo,
                rootDirectoryEntry
            )
            |> handleDirectory
        else
            do logger.Information (
                "Skipping handling of non-existent root directory {name}",
                rootDirectoryName
            )
}


/// Hashes the given file info and stores it into the database.
let private hashAndStoreFileInfo struct (state : DirectoryHandlingState, fileInfo : FileInfo) = async {
    do logger.Information (
        "Computing and storing hash for {path}",
        state.RemoveRootParentPathFrom fileInfo.FullName
    )
    let! hash = computeHash fileInfo
    let fileEntry = {
        Id          = 0
        DirectoryId = state.DirectoryEntry.Id
        Name        = fileInfo.Name
        Length      = fileInfo.Length
        Hash        = hash
    }
    do! state.Database.AddFile fileEntry |> Async.Ignore
}


/// Handles all root directories as specified in the given profile,
/// this means removing trash files if enabled, removing stale
/// database entries and hashing new files into the database with
/// the configured degree of parallelism.
let handleProfile configuration database profile =
    handleRootDirectories configuration database profile
    |> AsyncSeq.iterAsyncParallelThrottled configuration.DegreeOfParallelism hashAndStoreFileInfo

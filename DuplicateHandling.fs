/// Module containing functions to process duplicate file entries into
/// a model for the fancytree jQuery plugin.
[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module NikonTheThird.Krystallizer.DuplicateHandling


open FSharp.Control
open NikonTheThird.Krystallizer.Configuration
open NikonTheThird.Krystallizer.Database
open System.IO


/// Logger for this module.
let rec private logger = getModuleLogger <@ logger @>


/// Intermediate tree node for a directory entry.
/// Used to build a tree that is then converted into
/// a model for the fancytree jQuery plugin.
type [<Struct>] private DirectoryNode = {
    /// The directory entry represented by this node.
    DirectoryEntry : DirectoryEntry
    /// A map of all the subdirectory nodes. This is a map with the lowercase
    /// subdirectory name as the key so it is correctly sorted when iterated.
    SubdirectoryNodes : Map<string, DirectoryNode>
    /// A map of all the file entries. This is a map with the lowercase file
    /// name as the key so it is correctly sorted when iterated.
    DuplicateFileEntries : Map<string, FileEntry>
}


/// A model representing a directory that contains duplicate files
/// or contains subdirectories containing duplicate files.
type [<Struct>] DirectoryModel = {
    /// The key of the fancytree folder node. Set to the database directory id.
    Key : int32
    /// The displayed title of the fancytree folder node. Set to the directory name.
    Title : string
    /// The subfolders of the fancytree folder node.
    Children : DirectoryModel list
    /// The duplicate files of this directory, if available.
    Files : FilesModel option
    /// Always set to true to indicate to fancytree that this is a folder node.
    Folder : bool
    /// Additional CSS classes for the folder node:
    /// * contains-duplicates when this directory contains duplicate files.
    ExtraClasses : string
}


/// A model indicating that a directory contains duplicates.
and [<Struct>] FilesModel = {
    /// A list of all duplicate files in the directory.
    Duplicate : FileModel list
    /// A list of all nonduplicate file names in the directory.
    NonDuplicateNames : Set<string>
}


/// A model representing a duplicate file.
and [<Struct>] FileModel = {
    /// The database id of the duplicate file entry.
    Id : int32
    /// The database id of the directory the dulplicate file is in.
    DirectoryId : int32
    /// The name of the duplicate file.
    Name : string
    /// The other file entry ids that this file is equal to.
    DuplicateToIds : Set<int32>
}


/// Transforms an unsorted sequence of duplicate file entries into an unsorted
/// sequence of directory nodes for these file entries. Only directory nodes
/// are returned that contain at least one duplicate file entry.
let private createUnsortedDirectoryNodes (database : DatabaseConnection) =
    Seq.groupBy (fun ({ DirectoryId = directoryId} : FileEntry) -> directoryId)
    >> AsyncSeq.ofSeq
    >> AsyncSeq.mapAsync (fun (directoryId, fileEntries) -> async {
        let! directoryEntry = database.GetDirectoryById directoryId
        let fileEntryMap =
            fileEntries
            |> Seq.map (fun fileEntry -> fileEntry.Name.ToLowerInvariant (), fileEntry)
            |> Map.ofSeq
        return {
            DirectoryEntry       = directoryEntry
            SubdirectoryNodes    = Map.empty
            DuplicateFileEntries = fileEntryMap
        }
    })


/// Add parent directory nodes to the given list of unsorted directory nodes that are not in
/// the list yet. This is performed recursively, the final returned list of directory nodes
/// contains all nodes to build trees starting from the root directory nodes in the list.
let rec private introduceParentDirectoryNodes (database : DatabaseConnection) directoryNodes = async {
    /// The set of all parent directory ids (excluding empty ones) of the given directory nodes.
    let parentIdSet =
        directoryNodes
        |> Seq.choose (fun { DirectoryEntry = { ParentId = AsOption parentId } } -> parentId)
        |> Set.ofSeq
    /// The set of all directory ids of the given directory nodes.
    let directoryIdSet =
        directoryNodes
        |> Seq.map (fun { DirectoryEntry = { Id = id } } -> id)
        |> Set.ofSeq
    /// The set of all parent directory ids that do not have corresponding directory nodes
    /// in the list and have to be loaded from the database.
    let missingDirectoryIds = Set.difference parentIdSet directoryIdSet
    if Set.isEmpty missingDirectoryIds then
        // If there are no more missing parent directories, we are done.
        return directoryNodes
    else
        // There are missing parent directories, so load them and then attempt to
        // introduce new parent directory nodes for those.
        return! missingDirectoryIds
        |> AsyncSeq.ofSeq
        |> AsyncSeq.foldAsync (fun directoryNodes' missingDirectoryId -> async {
            let! missingDirectoryEntry = database.GetDirectoryById missingDirectoryId
            return {
                DirectoryEntry       = missingDirectoryEntry
                SubdirectoryNodes    = Map.empty
                DuplicateFileEntries = Map.empty
            } :: directoryNodes'
        }) directoryNodes
        |> Async.Bind (introduceParentDirectoryNodes database)
}


/// Recursively builds a tree of directory nodes for the given directory node
/// by populating its subdirectory nodes using the given parent id map. This
/// operation is applied to all subdirectory nodes as well, resulting in a
/// complete tree for the given directory node.
let rec private buildTreeForDirectoryNode directoryNodeParentIdMap directoryNode = {
    directoryNode with
        SubdirectoryNodes =
            directoryNodeParentIdMap
            |> Map.tryFind (ValueSome directoryNode.DirectoryEntry.Id)
            |> Option.defaultValue Seq.empty
            |> Seq.map (fun subdirectoryNode ->
                subdirectoryNode.DirectoryEntry.Name.ToLowerInvariant (),
                buildTreeForDirectoryNode directoryNodeParentIdMap subdirectoryNode
            )
            |> Map.ofSeq
}


/// Constructs a tree of directory nodes containing all duplicate file entries for
/// each root directory. This is constructed over all duplicate file entries
/// in all root directories regardless of the profile.
let private buildTreesForRootDirectoryNodes database duplicateFileEntries = asyncSeq {
    /// A flat list of all directory nodes containing duplicate file entries.
    let! unsortedDirectoryNodes =
        duplicateFileEntries
        |> createUnsortedDirectoryNodes database
        |> AsyncSeq.toListAsync
    do logger.Information (
        "{count} directory ids found, introducing parent directories",
        List.length unsortedDirectoryNodes
    )
    /// A flat list of all directory nodes containing duplicate file entries and their
    /// parent directory nodes up to root directory nodes.
    let! unsortedDirectoryNodes' =
        unsortedDirectoryNodes
        |> introduceParentDirectoryNodes database
    do logger.Information (
        "{count} parent directory ids added, building tree",
        List.length unsortedDirectoryNodes' - List.length unsortedDirectoryNodes
    )
    /// A map of all parent ids and their directory nodes.
    let directoryNodeParentIdMap =
        unsortedDirectoryNodes'
        |> Seq.groupBy (fun { DirectoryEntry = { ParentId = parentId } } -> parentId)
        |> Map.ofSeq
    yield! unsortedDirectoryNodes'
    |> Seq.filter (fun { DirectoryEntry = { ParentId = parentId } } -> ValueOption.isNone parentId)
    |> Seq.sortBy (fun { DirectoryEntry = { Name = name } } -> name.ToLowerInvariant ())
    |> AsyncSeq.ofSeq
    |> AsyncSeq.map (buildTreeForDirectoryNode directoryNodeParentIdMap)
}


/// Constructs a file model for the given file entry by looking up all other
/// duplicate files using the given duplicate entry map and the file hash.
let private generateModelForFileEntry duplicateFileEntryMap (fileEntry : FileEntry) = {
    Id             = fileEntry.Id
    DirectoryId    = fileEntry.DirectoryId
    Name           = fileEntry.Name
    DuplicateToIds =
        duplicateFileEntryMap
        |> Map.find fileEntry.Hash
        |> Seq.map (fun ({ Id = id } : FileEntry) -> id)
        |> Set.ofSeq
        |> Set.remove fileEntry.Id
}


/// Constructs a file model for the given map of duplicate directory file entries.
/// This model contains a list of all duplicate file models as well as a list
/// of all non-duplicate file names (which might be useful when a directory contains
/// mostly duplicates). If the given map contains no elements, no model is returned
/// since the directory is free of duplicates.
let private generateModelForFileEntries duplicateFileEntryMap directoryPath =
    Map.toSeq
    >> Seq.map (snd >> generateModelForFileEntry duplicateFileEntryMap)
    >> List.ofSeq
    >> function [] -> None | duplicateFileModels -> Some duplicateFileModels
    >> Option.map (fun duplicateFileModels ->
        /// A set of all the file names in the given directory.
        let allNames =
            directoryPath
            |> Directory.EnumerateFiles
            |> Seq.map Path.GetFileName
            |> Set.ofSeq
        /// A set of all duplicate file names in the given directory.
        let duplicateNames =
            duplicateFileModels
            |> Seq.map (fun { Name = name } -> name)
            |> Set.ofSeq
        {
            Duplicate         = duplicateFileModels
            NonDuplicateNames = Set.difference allNames duplicateNames
        }
    )


/// Constructs a directory model tree for each of the given directory nodes.
/// Each directory model will contain a duplicate file model only if it
/// actually contains duplicate files, otherwise it will be none.
let rec private generateModelForSubdirectoryNodes duplicateFileEntryMap directoryPath =
    Map.toSeq
    >> Seq.map (fun (_, subdirectoryNode) ->
        Path.Combine (directoryPath, subdirectoryNode.DirectoryEntry.Name)
        |> generateModelForDirectoryNode duplicateFileEntryMap subdirectoryNode
    )
    >> List.ofSeq


/// Constructs a directory model tree for the given directory node.
/// The directory model will contain a duplicate file model only if it
/// actually contains duplicate files, otherwise it will be none.
and private generateModelForDirectoryNode duplicateFileEntryMap directoryNode directoryPath =
    let files = directoryNode.DuplicateFileEntries |> generateModelForFileEntries duplicateFileEntryMap directoryPath
    {
        Key          = directoryNode.DirectoryEntry.Id
        Title        = directoryNode.DirectoryEntry.Name
        Children     = directoryNode.SubdirectoryNodes |> generateModelForSubdirectoryNodes duplicateFileEntryMap directoryPath
        Files        = files
        Folder       = true
        ExtraClasses = if Option.isSome files then "contains-duplicates" else ""
    }


/// Constructs a directory model for the given root directory node.
/// The returned model contains directory models down to all duplicate
/// file models within the root directory.
let private generateModelForRootDirectoryNode configuration duplicateFileEntryMap rootDirectoryNode =
    logger.Information (
        "Tree for root directory {name} built, generating model",
        rootDirectoryNode.DirectoryEntry.Name
    )
    Path.Combine (
        executingAssemblyDirectoryInfo.FullName,
        configuration.RootDirectoryParentPath,
        rootDirectoryNode.DirectoryEntry.Name
    )
    |> generateModelForDirectoryNode duplicateFileEntryMap rootDirectoryNode


/// Constructs directory models for all root directories containing duplicate
/// file entries in the database. The directory models contain subdirectory models
/// down to all duplicate file models within the specific root directory.
let buildModelsForRootDirectoryNodes configuration (database : DatabaseConnection) = asyncSeq {
    do logger.Information "Loading duplicate file entries from database"
    /// A list of all duplicate file entries currently in the database.
    let! duplicateFileEntries =
        database.GetDuplicateFiles ()
        |> AsyncSeq.toListAsync
    do logger.Information (
        "{count} duplicate file entries loaded, building duplicate file entry map",
        List.length duplicateFileEntries
    )
    /// A map grouping duplicate file entries by their hashes.
    let duplicateFileEntryMap =
        duplicateFileEntries
        |> Seq.groupBy (fun { Hash = hash } -> hash)
        |> Seq.map (fun (hash, duplicateFileEntries) -> hash, List.ofSeq duplicateFileEntries)
        |> Map.ofSeq
    do logger.Information "Duplicate file entry map built, building trees for root directories"
    yield! duplicateFileEntries
    |> buildTreesForRootDirectoryNodes database
    |> AsyncSeq.map (generateModelForRootDirectoryNode configuration duplicateFileEntryMap)
}

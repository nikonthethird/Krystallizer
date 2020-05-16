/// Module containing database access models and functions.
[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module NikonTheThird.Krystallizer.Database


open FSharp.Control
open Npgsql
open Serilog
open System
open System.Data.Common
open System.Threading


/// The directory model for the database.
type [<Struct>] DirectoryEntry = {
    /// The id of the directory. When inserting, the id
    /// will be set by the database.
    Id : int32
    /// The parent directory id of this directory, or none
    /// when the directory is a root directory.
    ParentId : int32 voption
    /// The name of the directory.
    Name : string
}


/// The file model for the database.
type [<Struct>] FileEntry = {
    /// The id of the file. When inserting, the id
    /// will be set by the database.
    Id : int32
    /// The id of the directory the file is in.
    DirectoryId : int32
    /// The name of the file.
    Name : string
    /// The length of the file in bytes.
    Length : int64
    /// The computed hash of the file contents.
    Hash : byte array
}


/// SQL statement to create the "Directories" table.
let [<Literal>] private CreateDirectoryTableSql = """
    create table if not exists "Directories" (
        "Id"       serial primary key,
        "ParentId" integer,
        "Name"     text not null,
        foreign key ("ParentId") references "Directories" ("Id") on delete cascade
    )
"""


/// SQL statement to create the "Files" table.
let [<Literal>] private CreateFileTableSql = """
    create table if not exists "Files" (
        "Id"          serial primary key,
        "DirectoryId" integer not null,
        "Name"        text not null,
        "Length"      bigint not null,
        "Hash"        bytea not null,
        foreign key ("DirectoryId") references "Directories" ("Id") on delete cascade
    )
"""


/// SQL statement to create an index for the "ParentId" column of
/// the "Directories" table. This index is used when looking for
/// all subdirectories of a particular directory.
let [<Literal>] private CreateDirectoryParentIdIndexSql = """
    create index if not exists "DirectoriesParentIdIndex"
    on "Directories" ("ParentId")
"""


/// SQL statement to create an index for the "DirectoryId" column
/// of the "Files" table. This index is used when looking for
/// all files in a particular directory.
let [<Literal>] private CreateFileDirectoryIdIndexSql = """
    create index if not exists "FilesDirectoryIdIndex"
    on "Files" ("DirectoryId")
"""


/// SQL statement to create an index for the "Hash" column
/// of the "Files" table. This index is used to find all
/// duplicate files.
let [<Literal>] private CreateFileHashIndexSql = """
    create index if not exists "FilesHashIndex"
    on "Files" ("Hash")
"""


/// SQL statement to insert a new directory entry.
let [<Literal>] private InsertDirectorySql = """
    insert into "Directories" (
        "ParentId",
        "Name"
    ) values (
        @parentId,
        @name
    ) returning "Id"
"""


/// SQL statement that selects all columns of the "Directories"
/// table and is used to construct other queries on this table.
let [<Literal>] private SelectDirectoryTemplateSql = """
    select  d."Id",
            d."ParentId",
            d."Name"
    from "Directories" d
"""


/// SQL statement to select a single directory entry by its id.
let [<Literal>] private SelectDirectoryByIdSql =
    SelectDirectoryTemplateSql + """
        where d."Id" = @id
    """


/// SQL statement to select a single directory entry by its parent
/// directory id and name. This combination is unique.
let [<Literal>] private SelectDirectoryByParentIdAndNameSql =
    SelectDirectoryTemplateSql + """
        where   (
                    (d."ParentId" = @parentId and @parentId is not null) or
                    (d."ParentId" is null and @parentId is null)
                ) and
                d."Name" = @name
    """


/// SQL statement that selects all subdirectory entries with
/// the same parent directory id.
let [<Literal>] private SelectDirectoriesByParentIdSql =
    SelectDirectoryTemplateSql + """
        where   (d."ParentId" = @parentId and @parentId is not null) or
                (d."ParentId" is null and @parentId is null)
    """


/// SQL statement to delete a directory entry.
/// Cascade delete removes all subdirectory entries and
/// file entries as well.
let [<Literal>] private DeleteDirectorySql = """
    delete from "Directories"
    where "Id" = @id
"""


/// SQL statement to insert a new file entry.
let [<Literal>] private InsertFileSql = """
    insert into "Files" (
        "DirectoryId",
        "Name",
        "Length",
        "Hash"
    ) values (
        @directoryId,
        @name,
        @length,
        @hash
    ) returning "Id"
"""


/// SQL statement that selects all columns of the "Files" 
/// table and is used to construct other queries on this table.
let [<Literal>] private SelectFileTemplateSql = """
    select  f."Id",
            f."DirectoryId",
            f."Name",
            f."Length",
            f."Hash"
    from "Files" f
"""


/// SQL statement to select a single file entry by its id.
let [<Literal>] private SelectFileByIdSql =
    SelectFileTemplateSql + """
        where f."Id" = @id
    """


/// SQL statement that selects all file entries in a
/// particular directory.
let [<Literal>] private SelectFilesByDirectoryIdSql =
    SelectFileTemplateSql + """
        where f."DirectoryId" = @directoryId
    """


/// SQL statement that selects all file entries that
/// have duplicate hashes.
let [<Literal>] private SelectDuplicateFilesSql =
    SelectFileTemplateSql + """
        join (
        	select inner_f."Hash"
        	from "Files" inner_f
        	group by inner_f."Hash"
        	having count(*) > 1
        ) d on f."Hash" = d."Hash"
    """


/// SQL statement to delete a file entry.
let [<Literal>] private DeleteFileSql = """
    delete from "Files"
    where "Id" = @id
"""


/// Represents an open database connection.
type DatabaseConnection private (connection : NpgsqlConnection) =

    static let logger = Log.ForContext<DatabaseConnection> ()
    let semaphore = new SemaphoreSlim 1

    /// Establish a connection to the database using the given
    /// connection string.
    static member EstablishConnection connectionString = async {
        do logger.Debug "Establishing a database connection"
        let connection = new NpgsqlConnection (connectionString)
        do! connection.OpenAsync () |> Async.AwaitTask
        return new DatabaseConnection (connection)
    }

    /// Reads a directory entry from the given data reader
    /// according to the columns in the directory select template.
    member inline private _.ReadDirectory (reader : DbDataReader) = {
        Id       = reader.GetInt32       0
        ParentId = reader.GetInt32Option 1
        Name     = reader.GetString      2
    }

    /// Reads a file entry from the given data reader
    /// according to the columns in the file select template.
    member inline private _.ReadFile (reader : DbDataReader) = {
        Id          = reader.GetInt32     0
        DirectoryId = reader.GetInt32     1
        Name        = reader.GetString    2
        Length      = reader.GetInt64     3
        Hash        = reader.GetByteArray 4
    }

    /// Creates all tables and indexes in the database
    /// if they do not exist.
    member _.CreateDatabaseStructure () = async {
        let createStatements = [
            CreateDirectoryTableSql
            CreateFileTableSql
            CreateDirectoryParentIdIndexSql
            CreateFileDirectoryIdIndexSql
            CreateFileHashIndexSql
        ]
        do! semaphore.WaitAsync () |> Async.AwaitTask
        try for createStatement in createStatements do
                use command = connection.CreateCommand ()
                do command.CommandText <- createStatement
                do! command.ExecuteNonQueryAsync () |> Async.AwaitTask |> Async.Ignore
        finally semaphore.Release () |> ignore
    }

    /// Adds a new directory entry to the database. The id of the given directory
    /// entry is ignored and a new directory entry with the correct id is returned.
    member _.AddDirectory ({ ParentId = parentId; Name = name } as directoryEntry) = async {
        do! semaphore.WaitAsync () |> Async.AwaitTask
        try use command = connection.CreateCommand ()
            do command.CommandText <- InsertDirectorySql
            do command.AddTypedOptionParameter (nameof parentId, parentId)
            do command.AddTypedParameter (nameof name, name)
            use! reader = command.ExecuteReaderAsync () |> Async.AwaitTask
            match! reader.ReadAsync () |> Async.AwaitTask with
            | true ->
                return { directoryEntry with Id = reader.GetInt32 0 }
            | false ->
                do logger.Error ("Could not add directory entry with parent id {parentId} and name {name}, no id returned", parentId, name)
                return failwithf "Could not add directory entry with parent id %A and name %s, no id returned" parentId name
        finally semaphore.Release () |> ignore
    }

    /// Returns a directory entry with the given id. If the directory entry does
    /// not exist, an exception is raised.
    member this.GetDirectoryById id = async {
        do! semaphore.WaitAsync () |> Async.AwaitTask
        try use command = connection.CreateCommand ()
            do command.CommandText <- SelectDirectoryByIdSql
            do command.AddTypedParameter<int32> (nameof id, id)
            use! reader = command.ExecuteReaderAsync () |> Async.AwaitTask
            match! reader.ReadAsync () |> Async.AwaitTask with
            | true ->
                return this.ReadDirectory reader
            | false ->
                do logger.Error ("Could not get directory entry with id {id}", id)
                return failwithf "Could not get directory entry with id %d" id
        finally semaphore.Release () |> ignore
    }

    /// Returns a directory entry with the given parent directory id and name.
    /// If the directory entry does not exist, none is returned.
    member this.TryGetDirectoryByParentIdAndName (parentId, name) = async {
        do! semaphore.WaitAsync () |> Async.AwaitTask
        try use command = connection.CreateCommand ()
            do command.CommandText <- SelectDirectoryByParentIdAndNameSql
            do command.AddTypedOptionParameter<int32> (nameof parentId, parentId)
            do command.AddTypedParameter<string> (nameof name, name)
            use! reader = command.ExecuteReaderAsync () |> Async.AwaitTask
            match! reader.ReadAsync () |> Async.AwaitTask with
            | true ->
                return this.ReadDirectory reader |> ValueSome
            | false ->
                return ValueNone
        finally semaphore.Release () |> ignore
    }

    /// Returns a directory entry with the given parent directory id and name.
    /// If no such directory entry exists, an exception is raised.
    member this.GetDirectoryByParentIdAndName (parentId, name) = async {
        match! this.TryGetDirectoryByParentIdAndName (parentId, name) with
        | ValueSome directory ->
            return directory
        | ValueNone ->
            do logger.Error ("Could not get directory entry with parent id {parentId} and name {name}", parentId, name)
            return failwithf "Could not get directory entry with parent id %A and name %s" parentId name
    }

    /// Returns all subdirectory entries of the given parent directory id.
    member this.GetDirectoriesByParentId parentId = asyncSeq {
        do! semaphore.WaitAsync () |> Async.AwaitTask
        try use command = connection.CreateCommand ()
            do command.CommandText <- SelectDirectoriesByParentIdSql
            do command.AddTypedOptionParameter<int32> (nameof parentId, parentId)
            use! reader = command.ExecuteReaderAsync () |> Async.AwaitTask
            while reader.ReadAsync () |> Async.AwaitTask do
                yield this.ReadDirectory reader
        finally semaphore.Release () |> ignore
    }

    /// Removes the directory entry with the given id.
    member _.RemoveDirectory id = async {
        do! semaphore.WaitAsync () |> Async.AwaitTask
        try use command = connection.CreateCommand ()
            do command.CommandText <- DeleteDirectorySql
            do command.AddTypedParameter<int32> (nameof id, id)
            do! command.ExecuteNonQueryAsync () |> Async.AwaitTask |> Async.Ignore
        finally semaphore.Release () |> ignore
    }

    /// Adds a new file entry to the database. The id of the given file
    /// entry is ignored and a new file entry with the correct id is returned.
    member _.AddFile ({ DirectoryId = directoryId; Name = name; Length = length; Hash = hash } as fileEntry) = async {
        do! semaphore.WaitAsync () |> Async.AwaitTask
        try use command = connection.CreateCommand ()
            do command.CommandText <- InsertFileSql
            do command.AddTypedParameter (nameof directoryId, directoryId)
            do command.AddTypedParameter (nameof name, name)
            do command.AddTypedParameter (nameof length, length)
            do command.AddTypedParameter (nameof hash, hash)
            use! reader = command.ExecuteReaderAsync () |> Async.AwaitTask
            match! reader.ReadAsync () |> Async.AwaitTask with
            | true ->
                return { fileEntry with Id = reader.GetInt32 0 }
            | false ->
                do logger.Error ("Could not add file entry with directory id {directoryId} and name {name}, no id returned", directoryId, name)
                return failwithf "Could not add file entry with directory id %d and name %s, no id returned" directoryId name
        finally semaphore.Release () |> ignore
    }

    /// Returns a file entry with the given id. If the file entry does
    /// not exist, an exception is raised.
    member this.GetFileById id = async {
        do! semaphore.WaitAsync () |> Async.AwaitTask
        try use command = connection.CreateCommand ()
            do command.CommandText <- SelectFileByIdSql
            do command.AddTypedParameter<int32> (nameof id, id)
            use! reader = command.ExecuteReaderAsync () |> Async.AwaitTask
            match! reader.ReadAsync () |> Async.AwaitTask with
            | true ->
                return this.ReadFile reader
            | false ->
                do logger.Error ("Could not get file entry with id {id}", id)
                return failwithf "Could not get file entry with id %d" id
        finally semaphore.Release () |> ignore
    }

    /// Returns all file entries that are in the given directory id.
    member this.GetFilesByDirectoryId directoryId = asyncSeq {
        do! semaphore.WaitAsync () |> Async.AwaitTask
        try use command = connection.CreateCommand ()
            do command.CommandText <- SelectFilesByDirectoryIdSql
            do command.AddTypedParameter<int32> (nameof directoryId, directoryId)
            use! reader = command.ExecuteReaderAsync () |> Async.AwaitTask
            while reader.ReadAsync () |> Async.AwaitTask do
                yield this.ReadFile reader
        finally semaphore.Release () |> ignore
    }

    /// Returns all file entries that have duplicate hashes.
    member this.GetDuplicateFiles () = asyncSeq {
        do! semaphore.WaitAsync () |> Async.AwaitTask
        try use command = connection.CreateCommand ()
            do command.CommandText <- SelectDuplicateFilesSql
            use! reader = command.ExecuteReaderAsync () |> Async.AwaitTask
            while reader.ReadAsync () |> Async.AwaitTask do
                yield this.ReadFile reader
        finally semaphore.Release () |> ignore
    }

    /// Removes the file entry with the given id.
    member _.RemoveFile id = async {
        do! semaphore.WaitAsync () |> Async.AwaitTask
        try use command = connection.CreateCommand ()
            do command.CommandText <- DeleteFileSql
            do command.AddTypedParameter<int32> (nameof id, id)
            do! command.ExecuteNonQueryAsync () |> Async.AwaitTask |> Async.Ignore
        finally semaphore.Release () |> ignore
    }

    interface IDisposable with

        /// Closes the database connection and cleans up resources.
        member _.Dispose () =
            logger.Debug "Closing the database connection"
            using connection ignore
            using semaphore  ignore

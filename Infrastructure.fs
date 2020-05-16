/// Module containing various items and helper functions used throughout the project.
[<AutoOpen; CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module NikonTheThird.Krystallizer.Infrastructure


open FSharp.Control
open FSharp.Quotations.Patterns
open Npgsql
open Serilog
open System
open System.Data.Common
open System.IO
open System.Reflection
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks


/// DirectoryInfo pointing to the path of the currently executing assembly.
/// This is used to resolve relative file and folder paths.
let executingAssemblyDirectoryInfo =
    let executingAssembly = Assembly.GetExecutingAssembly ()
    (FileInfo executingAssembly.Location).DirectoryName
    |> DirectoryInfo


/// Contains the date and time the program was started.
/// This is used to generate file names.
let programStartDateTime = DateTime.Now


/// JSON serializer options set up to handle F# types.
let jsonSerializerOptions =
    JsonSerializerOptions (PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
do jsonSerializerOptions.Converters.Add (
    JsonFSharpConverter (JsonUnionEncoding.Default ||| JsonUnionEncoding.UnwrapFieldlessTags)
)


/// Additional operations for async computation expressions.
type [<AbstractClass; Sealed>] Async private () =

    /// Like Async.AwaitTask, but for ValueTask.
    static member inline AwaitValueTask (task : ValueTask<_>) =
        task.AsTask ()
        |> Async.AwaitTask

    /// Waits for the given task to resolve and calls the mapping
    /// on its result. The mapping returns another task.
    static member inline Bind mapping task =
        async.Bind (task, mapping)

    /// Waits for the given task to resolve and calls the mapping
    /// on its result.
    static member inline Map mapping =
        Async.Bind (mapping >> async.Return)


/// Extensions for the async computation expression.
type AsyncBuilder with

    /// A version of for that accepts an asynchronous sequence.
    member inline _.For (sequence, body) = async {
        let! sequence = sequence
        for element in sequence do
            do! body element
    }


/// Extensions for the asyncSeq computation expression.
type AsyncSeq.AsyncSeqBuilder with

    /// A version of while that accepts an asynchronous guard.
    member inline _.While (guard, body) = asyncSeq {
        let! guardResult' = guard ()
        let mutable guardResult = guardResult'
        while guardResult do
            yield! body
            let! guardResult' = guard ()
            do guardResult <- guardResult'
    }


/// Extensions for the database column reader.
type DbDataReader with

    /// Read the given column index as a byte array.
    member inline this.GetByteArray index =
        this.GetValue index |> unbox<byte array>

    /// Read the given column index as an optional int32.
    member inline this.GetInt32Option index =
        match this.GetValue index with
        | value when Convert.IsDBNull value -> ValueNone
        | value -> unbox<int32> value |> ValueSome


/// Extensions for the SQL command builder.
type NpgsqlCommand with

    /// Adds a typed parameter to the command, which avoids boxing.
    member inline this.AddTypedParameter<'T> (name, value) =
        NpgsqlParameter<'T> (
            ParameterName = name,
            TypedValue = value
        )
        |> this.Parameters.Add
        |> ignore

    /// Adds a typed optional parameter to the command, which avoids boxing.
    member inline this.AddTypedOptionParameter<'T> (name, value) =
        match value with
        | ValueSome value -> this.AddTypedParameter<'T> (name, value)
        | ValueNone -> this.Parameters.AddWithValue (name, DBNull.Value) |> ignore


/// Extensions for value options.
[<RequireQualifiedAccess; CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module ValueOption =

    /// Converts the given value option to a regular option.
    let toOption = function
    | ValueSome value -> Some value
    | ValueNone       -> None


/// Active pattern to convert a value option to a regular option inside a match.
let (|AsOption|) = ValueOption.toOption


/// Returns a Serilog logger for the given module property expression.
let getModuleLogger = function
| PropertyGet (_, propertyInfo, _) -> Log.ForContext propertyInfo.DeclaringType
| _ -> raise (InvalidOperationException "Invalid logger expression")

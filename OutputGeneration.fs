/// Module containing functions to create a HTML file displaying duplicate
/// files in an understandable format to the user.
[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module NikonTheThird.Krystallizer.OutputGeneration


open NikonTheThird.Krystallizer.Configuration
open System.IO
open System.Text.Json


/// Logger for this module.
let rec private logger = getModuleLogger <@ logger @>


/// Generates an HTML file displaying the duplicate files in the
/// given directory models to the user.
let generateDuplicatesFile configuration rootDirectoryModels = async {
    do logger.Information "Generating the duplicates file"
    let duplicatesFilePath =
        Path.Combine (
            executingAssemblyDirectoryInfo.FullName,
            configuration.DuplicatesFilePath,
            sprintf "Duplicates-%s.html" (programStartDateTime.ToString "yyyy-MM-dd")
        )
    let templateFilePath = Path.Combine (executingAssemblyDirectoryInfo.FullName, "OutputTemplate.html")
    let! templateContent = File.ReadAllTextAsync templateFilePath |> Async.AwaitTask
    let templateParts = templateContent.Split ("window.duplicateData", 2)
    use writer = File.CreateText duplicatesFilePath
    do! writer.WriteAsync templateParts.[0] |> Async.AwaitTask
    do writer.Flush ()
    do! JsonSerializer.SerializeAsync (writer.BaseStream, rootDirectoryModels, jsonSerializerOptions) |> Async.AwaitTask
    do! writer.WriteAsync templateParts.[1] |> Async.AwaitTask
}

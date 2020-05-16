# Krystallizer

A command line tool that hashes files to find duplicates and produces HTML output.

You need .NET 5 to build:

```powershell
dotnet publish --configuration Release
```

Copy the contents of the `Bin/Release/net5.0` folder to some other location.
A `Configuration.json` file has to be placed into the same folder as `Krystallizer.dll`,
a configuration example looks like this:

```json
{
    "$schema": "./ConfigurationSchema.json",
    "connectionString": "Host=localhost; Port=5432; Database=krystallizer; User ID=my_user; Password=my_password; Application Name=Krystallizer;",
    "rootDirectoryParentPath": "..",
    "duplicatesFilePath": "..",
    "trashRegex": "(?i)^thumbs\\.db|\\.ds_store$",
    "degreeOfParallelism": 4,
    "profiles": {
        "default": {
            "rootDirectories": [
                "DirectoryToProcess1",
                "DirectoryToProcess2"
            ],
            "removeTrash": true
        }
    }
}
```

The configuration options are:

* **connectionString:** A connection string to a PostgreSQL database. The database should be empty,
  all necessary structure will be created the first time the program runs.
* **rootDirectoryParentPath:** A path relative from `Krystallizer.dll` to the parent path where
  all root directories reside. This path is used to resolve root directory names in profiles.
* **duplicatesFilePath:** A path relative from `Krystallizer.dll` to the directory where you want
  the file listing all found duplicates to be generated.
* **trashRegex:** A regular expression that matches all file names you consider trash files that
  should be removed and not hashed into the database. Trash removal only happens in profiles that
  have `removeTrash` set to `true`.
* **degreeOfParallelism:** File hashing happens in parallel. You can specify how many of these
  happen in parallel with this setting.
* **profiles:** Profiles allow you to specify which root directories to process at any given run
  of the program. The profile `default` should always exist and is the one used when you run without
  passing a profile name as an argument. For example, if you have added new files to only one root
  directory, you can specify a profile listing only this one root directory to avoid processing more
  than you have to. The profile configuration options are:
  * **rootDirectories:** Contains the name of all the root directories that should be processed when
    the profile is executed. The root directories are resolved relative to `Krystallizer.dll` and
    the `rootDirectoryParentPath` setting.
  * **removeTrash:** Indicates whether to remove files matching the trash regex as part of executing
    this profile.

To run the program, execute:

```powershell
dotnet Krystallizer.dll
# or
dotnet Krystallizer.dll profileName
```

This will start executing the specified profile, or the default profile if none was specified.
When the program is finished, a HTML file will be placed into the directory indicated by
`duplicatesFilePath`, which presents all found duplicates in a tree view.
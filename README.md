# UtilPack
Home of UtilPack - library with various useful and generic stuff for .NET.

## Portability
UtilPack is designed to be extremely portable.
Currently, it is target .NET 4 and .NET Standard 1.0.
The .NET 4 target is lacking any asynchronous code though.

## TODO
One of the most important thing to be done is adding proper unit tests for all the code.
Due to a bit too hasty development model, and the ancientness of the project (back when making unit tests in VS wasn't as simple as it is now, in 2017), there are no unit tests that would test just the code of UtilPack.
Thus the tests are something that need to be added very soon.


# Core - or just UtilPack
The UtilPack project is the core of other UtilPack-based projects residing in this repository.
It provides some of the most commonly used utilities, and also has some IL code to enable things are not possible with just C# code.

The UtilPack Core is located at http://www.nuget.org/packages/UtilPack

# UtilPack.JSON
This project uses various types located in UtilPack in order to provide fully asynchronous functionality to serialize and deserialize JSON objects (the JToken and its derivatives in Newtonsoft.JSON package).
The deserialization functionality is available through ```UtilPack.JSON.JTokenStreamReader``` class.
The serialization functionality is available through extension method to ```UtilPack.PotentiallyAsyncWriterLogic<IEnumerable<Char>, TSink>``` class.

The UtilPack.JSON is located at http://www.nuget.org/packages/UtilPack.JSON

# UtilPack.NuGet
This project uses the ```NuGet.Client``` library to resolve package dependencies and paths to assemblies in those dependencies.

The UtilPack.NuGet is located at http://www.nuget.org/packages/UtilPack.NuGet

# UtilPack.NuGet.MSBuild
This project exposes a task factory, which executes MSBuild tasks located in NuGet packages.
These tasks are allowed to have NuGet dependencies to other packages, and are not required to bundle all of their assembly dependencies in ```build``` folder.
Indeed, they can be built just like any normal NuGet package, containing their assembly files in ```lib``` directory, and no need to reference exactly the same runtime as where they will be executed (e.g. task built against .NET Standard 1.3 will be runnable in both Desktop and Core MSBuild runtimes).

See [more detailed documentation](Source/UtilPack.NuGet.MSBuild).

TODO this needs further testing on how it behaves when target task is compiled against different MSBuild version.

The UtilPack.NuGet.MSBuild is located at http://www.nuget.org/packages/UtilPack.NuGet.MSBuild

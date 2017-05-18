# UtilPack
Home of UtilPack - library with various useful and generic stuff for .NET.

## Portability
UtilPack is designed to be extremely portable.
Currently, it is target .NET 4 and .NET Standard 1.0.
The .NET 4 target is lacking any asynchronous code though.

# Core - or just UtilPack
The UtilPack project is the core of other UtilPack-based projects residing in this repository.
It provides some of the most commonly used utilities, and also has some IL code to enable things are not possible with just C# code.

# UtilPack.JSON
This project uses StreamStreamReaderWithResizableBuffer, IEncodingInfo, and StreamWriterWithResizableBuffer types located in UtilPack in order to provide fully asynchronous implementation to serialize and deserialize JSON objects (the JToken and its derivatives in Newtonsoft.JSON package).

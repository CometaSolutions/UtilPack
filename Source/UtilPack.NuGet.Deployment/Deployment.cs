﻿/*
 * Copyright 2017 Stanislav Muhametsin. All rights Reserved.
 *
 * Licensed  under the  Apache License,  Version 2.0  (the "License");
 * you may not use  this file  except in  compliance with the License.
 * You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed  under the  License is distributed on an "AS IS" BASIS,
 * WITHOUT  WARRANTIES OR CONDITIONS  OF ANY KIND, either  express  or
 * implied.
 *
 * See the License for the specific language governing permissions and
 * limitations under the License. 
 */
using Microsoft.Extensions.DependencyModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Protocol.Core.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UtilPack.NuGet.Deployment
{
   /// <summary>
   /// This class contains configurable implementation for deploying NuGet packages.
   /// Deploying in this context means restoring any missing packages, and copying and generating all the required files so that the package can be executed using <c>dotnet</c> tool.
   /// This means that only one framework of the package will be used.
   /// </summary>
   public class NuGetDeployment
   {
      private const String RUNTIME_CONFIG_FW_NAME = "runtimeOptions.framework.name";
      private const String RUNTIME_CONFIG_FW_VERSION = "runtimeOptions.framework.version";
      private const String RUNTIME_CONFIG_PROBING_PATHS = "runtimeOptions.additionalProbingPaths";
      private const String DEPS_EXTENSION = ".deps.json";
      private const String RUNTIME_CONFIG_EXTENSION = ".runtimeconfig.json";

      private readonly DeploymentConfiguration _config;

      /// <summary>
      /// Creates a new instance of <see cref="NuGetDeployment"/> with given <see cref="DeploymentConfiguration"/>.
      /// </summary>
      /// <param name="config">The deployment configuration.</param>
      /// <exception cref="ArgumentNullException">If <paramref name="config"/> is <c>null</c>.</exception>
      public NuGetDeployment( DeploymentConfiguration config )
      {
         this._config = ArgumentValidator.ValidateNotNull( nameof( config ), config );
      }

      /// <summary>
      /// Performs deployment asynchronously.
      /// </summary>
      /// <param name="nugetSettings">The NuGet settings to use.</param>
      /// <param name="targetDirectory">The directory where to place required files. If <c>null</c> or empty, then a new directory with randomized name will be created in temporary folder of the current user.</param>
      /// <param name="logger">The optional logger to use.</param>
      /// <param name="token">The optional cancellation token to use.</param>
      /// <returns>The full path to the assembly which should be executed, along with the resolved target framework as <see cref="NuGetFramework"/> object.</returns>
      /// <exception cref="ArgumentNullException">If <paramref name="nugetSettings"/> is <c>null</c>.</exception>
      public async Task<(String, NuGetFramework)> DeployAsync(
         ISettings nugetSettings,
         String targetDirectory,
         ILogger logger = null,
         CancellationToken token = default( CancellationToken )
         )
      {
         var processConfig = this._config;

         String assemblyToBeExecuted = null;
         NuGetFramework targetFW = null;
         using ( var sourceCache = new SourceCacheContext() )
         {
            if ( logger == null )
            {
               logger = NullLogger.Instance;
            }

            (var identity, var fwInfo, var entryPointAssembly) = await this.PerformInitialRestore(
               nugetSettings,
               sourceCache,
               new AgnosticFrameworkLoggerWrapper( logger ),
               token
               );

            if ( identity != null && fwInfo != null && !String.IsNullOrEmpty( entryPointAssembly ) )
            {
               targetFW = fwInfo.TargetFramework;
               using ( var restorer = new BoundRestoreCommandUser(
                  nugetSettings,
                  sourceCacheContext: sourceCache,
                  nugetLogger: logger,
                  thisFramework: targetFW,
                  leaveSourceCacheOpen: true
                  ) )
               {

                  (var lockFile, var runtimeConfig, var sdkPackageID, var sdkPackageVersion) = await this.PerformActualRestore(
                     restorer,
                     targetFW,
                     identity,
                     entryPointAssembly,
                     token
                     );

                  CreateTargetDirectory( targetDirectory );

                  switch ( processConfig.DeploymentKind )
                  {
                     case DeploymentKind.GenerateConfigFiles:
                        if ( targetFW.IsDesktop() )
                        {
                           // This is not supported for desktop framework
                           // TODO log warning
                           assemblyToBeExecuted = DeployByCopyingAssemblies(
                              restorer,
                              lockFile,
                              targetFW,
                              entryPointAssembly,
                              runtimeConfig,
                              sdkPackageID,
                              sdkPackageVersion,
                              targetDirectory
                              );
                        }
                        else
                        {
                           assemblyToBeExecuted = DeployByGeneratingConfigFiles(
                              lockFile,
                              identity,
                              entryPointAssembly,
                              runtimeConfig,
                              sdkPackageID,
                              sdkPackageVersion,
                              targetDirectory
                              );
                        }
                        break;
                     case DeploymentKind.CopyNonSDKAssemblies:
                        assemblyToBeExecuted = DeployByCopyingAssemblies(
                           restorer,
                           lockFile,
                           targetFW,
                           entryPointAssembly,
                           runtimeConfig,
                           sdkPackageID,
                           sdkPackageVersion,
                           targetDirectory
                           );
                        break;
                     default:
                        throw new NotSupportedException( $"Unrecognized deployment kind: {processConfig.DeploymentKind}." );
                  }
               }
            }
         }

         return (assemblyToBeExecuted, targetFW);
      }

      private async Task<(PackageIdentity, FrameworkSpecificGroup, String)> PerformInitialRestore(
         ISettings nugetSettings,
         SourceCacheContext sourceCache,
         ILogger logger,
         CancellationToken token
         )
      {
         var config = this._config;
         (PackageIdentity, FrameworkSpecificGroup, String) retVal = (null, null, null);
         using ( var restorer = new BoundRestoreCommandUser(
            nugetSettings,
            sourceCacheContext: sourceCache,
            nugetLogger: logger,
            thisFramework: NuGetFramework.AgnosticFramework, // This will cause to only the target package to be restored, and none of its dependencies
            leaveSourceCacheOpen: true
            ) )
         {
            var lockFile = await restorer.RestoreIfNeeded( config.ProcessPackageID, config.ProcessPackageVersion, token );

            var pathWithinRepository = lockFile.Libraries.FirstOrDefault()?.Path;
            var packagePath = restorer.ResolveFullPath( lockFile, pathWithinRepository );
            if ( !String.IsNullOrEmpty( packagePath ) )
            {

               FrameworkSpecificGroup[] libItems;
               PackageIdentity packageID;
               using ( var reader = new PackageFolderReader( packagePath ) )
               {
                  libItems = reader.GetLibItems().ToArray();
                  packageID = reader.GetIdentity();
               }

               var possibleAssemblies = libItems
                  .SelectMany( item => item.Items.Select( i => (item, i) ) )
                  .Where( tuple => PackageHelper.IsAssembly( tuple.Item2 ) )
                  .Select( tuple => (tuple.Item1, Path.GetFullPath( Path.Combine( packagePath, tuple.Item2 ) )) )
                  .ToArray();

               var targetFWString = config.ProcessFramework;
               NuGetFramework targetFW;
               if ( !String.IsNullOrEmpty( targetFWString ) )
               {
                  targetFW = NuGetFramework.ParseFolder( targetFWString );
                  possibleAssemblies = possibleAssemblies
                     .Where( t => t.Item1.TargetFramework.Equals( targetFW ) )
                     .ToArray();
               }

               if ( possibleAssemblies.Length > 0 )
               {
                  var possibleAssemblyPaths = possibleAssemblies.Select( tuple => tuple.Item2 ).ToArray();
                  var matchingAssembly = UtilPackNuGetUtility.GetAssemblyPathFromNuGetAssemblies(
                     packageID.Id,
                     possibleAssemblyPaths,
                     config.ProcessAssemblyPath,
                     p => File.Exists( p )
                     );

                  if ( !String.IsNullOrEmpty( matchingAssembly ) )
                  {
                     var assemblyInfo = possibleAssemblies[Array.IndexOf( possibleAssemblyPaths, matchingAssembly )];
                     retVal = (packageID, assemblyInfo.Item1, assemblyInfo.Item2);
                  }

               }
            }
         }

         return retVal;
      }

      private async Task<(LockFile, JToken, String, String)> PerformActualRestore(
         BoundRestoreCommandUser restorer,
         NuGetFramework targetFramework,
         PackageIdentity identity,
         String entryPointAssemblyPath,
         CancellationToken token
         )
      {
         var config = this._config;
         var lockFile = await restorer.RestoreIfNeeded( identity.Id, identity.Version.ToNormalizedString(), token );
         var sdkPackageContainsAllPackagesAsAssemblies = this._config.SDKPackageContainsAllPackagesAsAssemblies;
         JToken runtimeConfig = null;
         String sdkPackageID = null;
         String sdkPackageVersion = null;
         var runtimeConfigPath = Path.ChangeExtension( entryPointAssemblyPath, RUNTIME_CONFIG_EXTENSION );
         if ( File.Exists( runtimeConfigPath ) )
         {
            try
            {
               using ( var streamReader = new StreamReader( File.OpenRead( runtimeConfigPath ) ) )
               using ( var jsonReader = new Newtonsoft.Json.JsonTextReader( streamReader ) )
               {
                  runtimeConfig = JToken.ReadFrom( jsonReader );
               }
               sdkPackageID = ( runtimeConfig.SelectToken( RUNTIME_CONFIG_FW_NAME ) as JValue )?.Value?.ToString();
               sdkPackageVersion = ( runtimeConfig.SelectToken( RUNTIME_CONFIG_FW_VERSION ) as JValue )?.Value?.ToString();
            }
            catch
            {
               // Ignore
            }
         }

         sdkPackageID = UtilPackNuGetUtility.GetSDKPackageID( targetFramework, config.ProcessSDKFrameworkPackageID ?? sdkPackageID );
         sdkPackageVersion = UtilPackNuGetUtility.GetSDKPackageVersion( targetFramework, sdkPackageID, config.ProcessSDKFrameworkPackageVersion ?? sdkPackageVersion );

         var sdkPackages = new HashSet<String>( lockFile.Targets[0].GetAllDependencies(
            sdkPackageID.Singleton()
            )
            .Select( lib => lib.Name )
            );

         // In addition, check all compile assemblies from sdk package (e.g. Microsoft.NETCore.App )
         // Starting from 2.0.0, all assemblies from all dependent packages are marked as compile-assemblies stored in sdk package.
         if ( sdkPackageContainsAllPackagesAsAssemblies.IsTrue() ||
            ( !sdkPackageContainsAllPackagesAsAssemblies.HasValue && sdkPackageID == UtilPackNuGetUtility.SDK_PACKAGE_NETCORE && Version.TryParse( sdkPackageVersion, out var sdkPkgVer ) && sdkPkgVer.Major >= 2 )
            )
         {
            var sdkPackageLibrary = lockFile.Targets[0].Libraries.FirstOrDefault( l => l.Name == sdkPackageID );
            if ( sdkPackageLibrary != null )
            {
               sdkPackages.UnionWith( sdkPackageLibrary.CompileTimeAssemblies.Select( cta => Path.GetFileNameWithoutExtension( cta.Path ) ) );
            }
         }

         // Actually -> return LockFile, but modify it so that sdk packages are removed
         var targetLibs = lockFile.Targets[0].Libraries;
         for ( var i = 0; i < targetLibs.Count; )
         {
            var curLib = targetLibs[i];
            var contains = sdkPackages.Contains( curLib.Name );
            if ( contains
               || (
                  ( curLib.RuntimeAssemblies.Count <= 0 || curLib.RuntimeAssemblies.All( ass => ass.Path.EndsWith( "_._" ) ) )
                  && curLib.RuntimeTargets.Count <= 0
                  && curLib.ResourceAssemblies.Count <= 0
                  && curLib.NativeLibraries.Count <= 0
                  )
               )
            {
               targetLibs.RemoveAt( i );
               if ( !contains )
               {
                  sdkPackages.Add( curLib.Name );
               }
            }
            else
            {
               ++i;
            }
         }

         var libs = lockFile.Libraries;
         for ( var i = 0; i < libs.Count; )
         {
            var curLib = libs[i];
            if ( sdkPackages.Contains( curLib.Name ) )
            {
               libs.RemoveAt( i );
            }
            else
            {
               ++i;
            }
         }

         return (lockFile, runtimeConfig, sdkPackageID, sdkPackageVersion);
      }

      private static String DeployByGeneratingConfigFiles(
         LockFile lockFile,
         PackageIdentity identity,
         String epAssemblyPath,
         JToken runtimeConfig,
         String sdkPackageID,
         String sdkPackageVersion,
         String targetPath
         )
      {
         // Create DependencyContext, which will be the contents of our .deps.json file
         // deps.json file is basically lock file, with a bit different structure and less information
         // So we can generate it from our lock file.
         var target = lockFile.Targets[0];
         var allLibs = lockFile.Libraries
            .ToDictionary( lib => lib.Name, lib => lib );
         // TODO at least in .NET Core 2.0, all the SDK assemblies ('trusted' assemblies) will *not* have "runtime" entry in their library json.
         var ctx = new DependencyContext(
            new TargetInfo( target.Name, null, null, true ), // portable will be false for native deployments, TODO detect that
            new CompilationOptions( Empty<String>.Enumerable, null, null, null, null, null, null, null, null, null, null, null ),
            Empty<CompilationLibrary>.Enumerable,
            target.Libraries.Select( targetLib =>
            {
               var lib = allLibs[targetLib.Name];
               var hash = lib.Sha512;
               if ( !String.IsNullOrEmpty( hash ) )
               {
                  hash = "sha512-" + hash;
               }
               return new RuntimeLibrary(
                   lib.Type,
                   lib.Name.ToLowerInvariant(),
                   lib.Version.ToNormalizedString(),
                   hash,
                   new RuntimeAssetGroup( "", targetLib.RuntimeAssemblies.Select( ra => ra.Path ) ).Singleton().Concat( TransformRuntimeTargets( targetLib.RuntimeTargets, "runtime" ) ).ToList(),
                   new RuntimeAssetGroup( "", targetLib.NativeLibraries.Select( ra => ra.Path ) ).Singleton().Concat( TransformRuntimeTargets( targetLib.RuntimeTargets, "native" ) ).ToList(),
                   targetLib.ResourceAssemblies.Select( ra => new ResourceAssembly( ra.Path, ra.Properties["locale"] ) ).ToList(),
                   targetLib.Dependencies
                     .Where( dep => allLibs.ContainsKey( dep.Id ) )
                     .Select( dep => new Dependency( dep.Id.ToLowerInvariant(), allLibs[dep.Id].Version.ToNormalizedString() ) ),
                   true,
                   lib.Path, // Specify path even for EP package, if it happens to consist of multiple assemblies
                   lib.Files.FirstOrDefault( f => f.EndsWith( "sha512" ) )
                   );
            } ),
            Empty<RuntimeFallbacks>.Enumerable // TODO proper generation of this, prolly related to native stuff
            );

         // Copy EP Assembly
         var targetAssembly = Path.Combine( targetPath, Path.GetFileName( epAssemblyPath ) );
         File.Copy( epAssemblyPath, targetAssembly, true );

         // Write .deps.json file
         // The .deps.json extension is in Microsoft.Extensions.DependencyModel.DependencyContextLoader as a const field, but it is private... :/
         using ( var fs = File.Open( Path.ChangeExtension( targetAssembly, DEPS_EXTENSION ), FileMode.Create, FileAccess.Write, FileShare.None ) )
         {
            new DependencyContextWriter().Write( ctx, fs );
         }

         // Write runtimeconfig.json file
         WriteRuntimeConfigFile(
            runtimeConfig,
            sdkPackageID,
            sdkPackageVersion,
            lockFile,
            targetAssembly
            );
         return targetAssembly;
      }

      private static IEnumerable<RuntimeAssetGroup> TransformRuntimeTargets( IEnumerable<LockFileRuntimeTarget> runtimeTargets, String key )
      {
         return runtimeTargets
            .GroupBy( rt => rt.Runtime )
            .Where( grp => grp.Any( rtLib => String.Equals( key, rtLib.AssetType, StringComparison.OrdinalIgnoreCase ) ) )
            .Select( grp => new RuntimeAssetGroup( grp.Key, grp.Where( rtLib => String.Equals( key, rtLib.AssetType, StringComparison.OrdinalIgnoreCase ) ).Select( rtLib => rtLib.Path ) ) );
      }

      private static String DeployByCopyingAssemblies(
         BoundRestoreCommandUser restorer,
         LockFile lockFile,
         NuGetFramework targetFW,
         String epAssemblyPath,
         JToken runtimeConfig,
         String sdkPackageID,
         String sdkPackageVersion,
         String targetPath
         )
      {
         var allAssemblyPaths = restorer.ExtractAssemblyPaths(
            lockFile,
            ( packageID, assemblies ) => assemblies
            ).Values
            .SelectMany( v => v )
            .Select( p => Path.GetFullPath( p ) )
            .ToArray();

         // TODO flat copy will cause problems for assemblies with same simple name but different public key token
         // We need to put conflicting files into separate directories and generate appropriate runtime.config file (or .(dll|exe).config for desktop frameworks!)
         Parallel.ForEach( allAssemblyPaths, curPath => File.Copy( curPath, Path.Combine( targetPath, Path.GetFileName( curPath ) ), false ) );

         var targetAssemblyName = Path.Combine( targetPath, Path.GetFileName( epAssemblyPath ) );

         if ( targetFW.IsDesktop() )
         {
            // TODO .config file for conflicting file names
         }
         else
         {
            // We have to generate runtimeconfig.file (pass 'null' as LockFile to disable probing path section creation)
            WriteRuntimeConfigFile(
               runtimeConfig,
               sdkPackageID,
               sdkPackageVersion,
               null,
               targetAssemblyName
               );
         }

         return targetAssemblyName;
      }

      private static void CreateTargetDirectory(
         String targetDirectory
         )
      {
         if ( String.IsNullOrEmpty( targetDirectory ) )
         {
            targetDirectory = Path.Combine( Path.GetTempPath(), "NuGetProcess_" + Guid.NewGuid() );
         }

         if ( !Directory.Exists( targetDirectory ) )
         {
            Directory.CreateDirectory( targetDirectory );
         }
      }

      private static void WriteRuntimeConfigFile(
         JToken runtimeConfig,
         String sdkPackageID,
         String sdkPackageVersion,
         LockFile lockFile,
         String targetAssemblyPath
         )
      {
         // Unfortunately, there doesn't seem to be API for this like there is Microsoft.Extensions.DependencyModel for .deps.json file... :/
         // So we need to directly manipulate JSON structure.
         if ( runtimeConfig == null )
         {
            runtimeConfig = new JObject();
         }
         runtimeConfig.SetToken( RUNTIME_CONFIG_FW_NAME, sdkPackageID );
         runtimeConfig.SetToken( RUNTIME_CONFIG_FW_VERSION, sdkPackageVersion );
         if ( lockFile != null )
         {
            var probingPaths = ( runtimeConfig.SelectToken( RUNTIME_CONFIG_PROBING_PATHS ) as JArray ) ?? new JArray();
            probingPaths.AddRange( lockFile.PackageFolders.Select( folder =>
            {
               var packageFolder = Path.GetFullPath( folder.Path );
               // We must strip trailing '\' or '/', otherwise probing will fail
               var lastChar = packageFolder[packageFolder.Length - 1];
               if ( lastChar == Path.DirectorySeparatorChar || lastChar == Path.AltDirectorySeparatorChar )
               {
                  packageFolder = packageFolder.Substring( 0, packageFolder.Length - 1 );
               }

               return (JToken) packageFolder;
            } ) );
            runtimeConfig.SetToken( RUNTIME_CONFIG_PROBING_PATHS, probingPaths );
         }

         File.WriteAllText(
            Path.ChangeExtension( targetAssemblyPath, RUNTIME_CONFIG_EXTENSION ),
            runtimeConfig.ToString( Formatting.Indented ), // Runtimeconfig is small file usually so just use this instead of filestream + textwriter + jsontextwriter combo.
            new UTF8Encoding( false, false ) // The Encoding.UTF8 emits byte order mark, which we don't want to do
            );
      }
   }

   /// <summary>
   /// This configuration provides a way to get information for deploying a single NuGet package.
   /// </summary>
   /// <seealso cref="DefaultDeploymentConfiguration"/>
   public interface DeploymentConfiguration
   {
      /// <summary>
      /// Gets the package ID of the package to be deployed.
      /// </summary>
      /// <value>The package ID of the package to be deployed.</value>
      String ProcessPackageID { get; }

      /// <summary>
      /// Gets the package version of the package to be deployed.
      /// </summary>
      /// <value>The package version of the package to be deployed.</value>
      /// <remarks>
      /// If this property is <c>null</c> or empty string, then NuGet source will be queried for the newest version.
      /// </remarks>
      String ProcessPackageVersion { get; }

      /// <summary>
      /// Gets the framework name (the folder name of the 'lib' folder within NuGet package) which should be deployed.
      /// </summary>
      /// <value>The framework name (the folder name of the 'lib' folder within NuGet package) which should be deployed.</value>
      /// <remarks>
      /// This property will not be used for NuGet packages with only one framework.
      /// </remarks>
      String ProcessFramework { get; }

      /// <summary>
      /// Gets the path within the package where the entrypoint assembly resides.
      /// </summary>
      /// <value>The path within the package where the entrypoint assembly resides.</value>
      /// <remarks>
      /// This property will not be used for NuGet packages with only one assembly.
      /// </remarks>
      String ProcessAssemblyPath { get; }

      /// <summary>
      /// Gets the package ID of the SDK of the framework of the NuGet package.
      /// </summary>
      /// <value>The package ID of the SDK of the framework of the NuGet package.</value>
      /// <remarks>
      /// If this property is <c>null</c> or empty string, then <see cref="NuGetDeployment"/> will try to use automatic detection of SDK package ID.
      /// </remarks>
      String ProcessSDKFrameworkPackageID { get; }

      /// <summary>
      /// Gets the package version of the SDK of the framework of the NuGet package.
      /// </summary>
      /// <value>The package version of the SDK of the framework of the NuGet package.</value>
      /// <remarks>
      /// If this property is <c>null</c> or empty string, then <see cref="NuGetDeployment"/> will try to use automatic detection of SDK package version.
      /// </remarks>
      String ProcessSDKFrameworkPackageVersion { get; }

      /// <summary>
      /// Gets the deployment kind.
      /// </summary>
      /// <value>The deployment kind.</value>
      /// <seealso cref="Deployment.DeploymentKind"/>
      DeploymentKind DeploymentKind { get; }

      /// <summary>
      /// Gets the information about SDK NuGet package (e.g. <c>"Microsoft.NETCore.App"</c>) related to how the SDK assemblies are relayed.
      /// </summary>
      /// <value>The information about SDK NuGet package (e.g. <c>"Microsoft.NETCore.App"</c>) related to how the SDK assemblies are relayed.</value>
      /// <remarks>
      /// Setting this to <c>true</c> will force the SDK package logic to assume that all compile time assemblies are package IDs of SDK sub-packages, thus affecting SDK package resolving logic.
      /// Setting this to <c>false</c> will force the SDK package logic to assume that main SDK package only has the assemblies that are exposed via NuGet package dependency chain.
      /// Leaving this unset (<c>null</c>) will use auto-detection (which will use <c>true</c> when deploying for .NET Core 2.0+, and will use <c>false</c> when deploying for other frameworks).
      /// </remarks>
      Boolean? SDKPackageContainsAllPackagesAsAssemblies { get; }
   }

   /// <summary>
   /// This enumeration controls which files are copied and generated during deployment process of <see cref="NuGetDeployment.DeployAsync"/>.
   /// </summary>
   public enum DeploymentKind
   {
      /// <summary>
      /// This value indicates that only the entrypoint assembly will be copied to the target directory, and <c>.deps.json</c> file will be generated, along with <c>.runtimeconfig.json</c> file.
      /// Those files will contain required information so that dotnet process will know to resolve dependency assemblies.
      /// This way the IO load by the deployment process will be kept at minimum.
      /// However, the dotnet process will then lock the DLL files in your package repository, as they are loaded directly from there.
      /// </summary>
      GenerateConfigFiles,

      /// <summary>
      /// This value indicates that entrypoint assembly along with all the non-SDK dependencies will be copied to the target folder.
      /// The <c>.deps.json</c> file will not be generated, but the <c>.runtimeconfig.json</c> file for .NET Core and <c>.exe.config</c> file for the .NET Desktop will be generated.
      /// The IO load may become heavy in this scenario, since possibly a lot of files may need to be copied.
      /// But with this deployment kind, the dotnet won't lock DLL files in your package repository.
      /// </summary>
      CopyNonSDKAssemblies
   }

   /// <summary>
   /// This class provides easy-to-use implementation of <see cref="DeploymentConfiguration"/>.
   /// </summary>
   public class DefaultDeploymentConfiguration : DeploymentConfiguration
   {
      /// <inheritdoc />
      public String ProcessPackageID { get; set; }

      /// <inheritdoc />
      public String ProcessPackageVersion { get; set; }

      /// <inheritdoc />
      public String ProcessAssemblyPath { get; set; }

      /// <inheritdoc />
      public String ProcessFramework { get; set; }

      /// <inheritdoc />
      public String ProcessSDKFrameworkPackageID { get; set; }

      /// <inheritdoc />
      public String ProcessSDKFrameworkPackageVersion { get; set; }

      /// <inheritdoc />
      public DeploymentKind DeploymentKind { get; set; }

      /// <inheritdoc />
      public Boolean? SDKPackageContainsAllPackagesAsAssemblies { get; set; }
   }


}

internal static class E_UtilPack
{
   internal static void SetToken( this JToken obj, String path, JToken value )
   {
      var existing = obj.SelectToken( path );
      if ( existing == null )
      {
         var cur = (JObject) obj;
         foreach ( var prop in path.Split( '.' ) )
         {
            if ( cur[prop] == null )
            {
               cur.Add( new JProperty( prop, new JObject() ) );
            }
            cur = (JObject) cur[prop];
         }
         cur.Replace( value );
      }
      else
      {
         existing.Replace( value );
      }
   }
}
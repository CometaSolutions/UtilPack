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
#if NETSTANDARD1_5
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace UtilPack
{
   /// <summary>
   /// This class allows to explicitly load assembly from given location (e.g. file path), and keep track of loaded assemblies.
   /// Furthermore, the load of dependent assemblies (which must be done since <see cref="System.Runtime.Loader.AssemblyLoadContext"/> loads assemblies lazily) is done in controlled and customizable way.
   /// </summary>
   /// <remarks>
   /// This class is not safe to be used concurrently.
   /// The <see cref="IDisposable.Dispose"/> method will unregister the callback from <see cref="System.Runtime.Loader.AssemblyLoadContext.Resolving"/>.
   /// </remarks>
   public class ExplicitAssemblyLoader : AbstractDisposable
   {
      private sealed class LoadedAssemblyInfo
      {
         public LoadedAssemblyInfo(
            String originalPath,
            String loadedPath,
            Assembly assembly
            )
         {
            this.OriginalPath = originalPath;
            this.LoadedPath = loadedPath;
            this.Assembly = assembly;
         }

         public String OriginalPath { get; }

         public String LoadedPath { get; }

         public Assembly Assembly { get; }
      }

      private readonly System.Runtime.Loader.AssemblyLoadContext _assemblyLoader;

      private readonly IDictionary<String, LoadedAssemblyInfo> _assembliesByOriginalPath;
      private readonly ISet<String> _allDiscoveredDependencies;

      private String _currentAssemblyPath;

      private readonly Func<String, String> _assemblyPathProcessor;
      private readonly Func<String, AssemblyName, IEnumerable<String>> _candidatePathGetter;

      /// <summary>
      /// Creates new instance of <see cref="ExplicitAssemblyLoader"/> with given optional customizers.
      /// </summary>
      /// <param name="assemblyPathProcessor">The callback to process the assembly path from which to actually load assembly. Only invoked when the file exists. Receives original assembly path as argument.</param>
      /// <param name="candidatePathGetter">The callback to scan through potential assembly locations. Receives referencing assembly path and assembly name reference as arguments. By default, only the <c>assembly_name.dll</c> is scanned.</param>
      /// <param name="assemblyLoadContext">The actual <see cref="System.Runtime.Loader.AssemblyLoadContext"/> to use. By default the <see cref="System.Runtime.Loader.AssemblyLoadContext.Default"/> is used.</param>
      /// <remarks>
      /// The <see cref="System.Runtime.Loader.AssemblyLoadContext.Resolving"/> event is registered right away in this constructor.
      /// The <paramref name="assemblyPathProcessor"/> may e.g. copy the assembly file from original location to some other location and return the copied path, thus behaving somewhat like shadow copying.
      /// </remarks>
      public ExplicitAssemblyLoader(
         Func<String, String> assemblyPathProcessor = null,
         Func<String, AssemblyName, IEnumerable<String>> candidatePathGetter = null,
         System.Runtime.Loader.AssemblyLoadContext assemblyLoadContext = null
         )
      {
         this._assemblyPathProcessor = assemblyPathProcessor;
         this._candidatePathGetter = candidatePathGetter ?? DefaultGetAssemblyCandidatePaths;
         this._assembliesByOriginalPath = new Dictionary<String, LoadedAssemblyInfo>();
         this._allDiscoveredDependencies = new HashSet<String>();
         this._assemblyLoader = assemblyLoadContext ?? System.Runtime.Loader.AssemblyLoadContext.Default;
         this._assemblyLoader.Resolving += _assemblyLoader_Resolving;
      }

      private Assembly _assemblyLoader_Resolving(
         System.Runtime.Loader.AssemblyLoadContext context,
         AssemblyName assemblyName
         )
      {
         Assembly retVal = null;
         if ( !String.IsNullOrEmpty( this._currentAssemblyPath ) )
         {
            var assemblyPath = this.GetFirstExistingAssemblyPath( this._currentAssemblyPath, assemblyName );

            if ( !String.IsNullOrEmpty( assemblyPath ) )
            {
               retVal = this.LoadLibraryAssembly( assemblyPath, out Boolean actuallyLoaded ).Assembly;
            }
         }

         return retVal;
      }

      /// <summary>
      /// Loads the assembly from given location, if it is not already loaded.
      /// </summary>
      /// <param name="location">The location to load assembly from. Is case-sensitive.</param>
      /// <remarks>
      /// This method recursively loads all dependencies of the assembly, if actua load is performed.
      /// </remarks>
      public Assembly LoadAssemblyFrom( String location )
      {
         var assemblyPath = System.IO.Path.GetFullPath( location );

         var assembly = this.LoadLibraryAssembly( assemblyPath, out Boolean actuallyLoaded );

         if ( actuallyLoaded )
         {
            // Load recursively all dependant assemblies right here, since the loader is lazy, and if the dependant assembly load happens later, our callback will be gone.
            this.LoadDependenciesRecursively( assembly );
         }

         return assembly.Assembly;
      }

      /// <summary>
      /// Checks whether the assembly at given location has been loaded by this <see cref="ExplicitAssemblyLoader"/>.
      /// </summary>
      /// <param name="location">The location of the assembly.</param>
      /// <returns><c>true</c> if assebmly at given location has been loaded by this <see cref="ExplicitAssemblyLoader"/>; <c>false</c> otherwise.</returns>
      public Boolean IsAssemblyLoaded( String location )
      {
         return this._assembliesByOriginalPath.ContainsKey( location );
      }

      /// <summary>
      /// Unloads the handler, which was registered by constructor, from <see cref="System.Runtime.Loader.AssemblyLoadContext.Resolving"/> event.
      /// </summary>
      /// <param name="disposing">Whether the managed disposing is going on.</param>
      protected override void Dispose( Boolean disposing )
      {
         if ( disposing )
         {
            this._assemblyLoader.Resolving -= this._assemblyLoader_Resolving;
         }
      }

      private void LoadDependenciesRecursively(
         LoadedAssemblyInfo loadedAssembly
         )
      {
         var assemblies = this._assembliesByOriginalPath;

         var assembliesToProcess = new List<LoadedAssemblyInfo>
         {
            loadedAssembly
         };
         var addedThisRound = new List<LoadedAssemblyInfo>();
         do
         {
            addedThisRound.Clear();

            foreach ( var loadedInfo in assembliesToProcess )
            {
               this._allDiscoveredDependencies.Add( loadedInfo.OriginalPath );

               foreach ( var aRef in loadedInfo.Assembly.GetReferencedAssemblies() )
               {
                  var curRef = aRef;
                  var oldCount = assemblies.Count;
                  var originalPath = loadedInfo.OriginalPath;
                  var assemblyPath = this.GetFirstExistingAssemblyPath( originalPath, curRef );

                  // We *must* use loading by assembly name here - otherwise we end up with multiple loaded assemblies with the same assembly name!
                  Assembly curAssembly = null;
                  try
                  {
                     curAssembly = this.LoadAssemblyReference(
                        originalPath,
                        curRef
                        );
                  }
                  catch
                  {
                     // Ignore
                  }

                  if ( curAssembly != null && System.IO.File.Exists( assemblyPath ) )
                  {

                     var newCount = assemblies.Count;

                     if ( newCount > oldCount )
                     {
                        addedThisRound.Add( this._assembliesByOriginalPath[assemblyPath] );
                     }

                     this._allDiscoveredDependencies.Add( assemblyPath );
                  }

               }
            }

            assembliesToProcess.Clear();
            assembliesToProcess.AddRange( addedThisRound );
         } while ( addedThisRound.Count > 0 );

      }

      private LoadedAssemblyInfo LoadLibraryAssembly(
         String originalPathParam,
         out Boolean actuallyLoaded
         )
      {
         var assemblies = this._assembliesByOriginalPath;
         var oldCount = assemblies.Count;
         var retVal = assemblies.GetOrAdd_NotThreadSafe( originalPathParam, originalPath =>
         {
            var processed = this._assemblyPathProcessor?.Invoke( originalPath );
            if ( String.IsNullOrEmpty( processed ) )
            {
               processed = originalPath;
            }

            return new LoadedAssemblyInfo(
               originalPath,
               processed,
               this.LoadAssemblyReference( originalPath, processed )
               );
         } );
         actuallyLoaded = assemblies.Count > oldCount;

         return retVal;
      }

      private Assembly LoadAssemblyReference(
         String referencingAssemblyOriginalPath,
         EitherOr<String, AssemblyName> assemblyRef
         )
      {
         var oldVal = this._currentAssemblyPath;
         this._currentAssemblyPath = referencingAssemblyOriginalPath;
         try
         {
            return assemblyRef.IsFirst ? this._assemblyLoader.LoadFromAssemblyPath( assemblyRef.First ) : this._assemblyLoader.LoadFromAssemblyName( assemblyRef.Second );
         }
         finally
         {
            this._currentAssemblyPath = oldVal;
         }
      }

      private static IEnumerable<String> DefaultGetAssemblyCandidatePaths(
         String referencingAssemblyOriginalPath,
         AssemblyName assemblyName
         )
      {
         var assemblyBasePath = System.IO.Path.Combine( System.IO.Path.GetDirectoryName( referencingAssemblyOriginalPath ), assemblyName.Name );

         // TODO something else than .dll in the future...? 
         yield return assemblyBasePath + ".dll";
      }

      private String GetFirstExistingAssemblyPath(
         String referencingAssemblyOriginalPath,
         AssemblyName assemblyName
         )
      {
         return this._candidatePathGetter( referencingAssemblyOriginalPath, assemblyName ).FirstOrDefault( aPath => System.IO.File.Exists( aPath ) );
      }

   }
}
#endif
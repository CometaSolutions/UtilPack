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
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UtilPack;

using TTaskExt = System.Threading.Tasks.
#if NET40
   TaskEx
#else
   Task
#endif
   ;

namespace UtilPack
{
   /// <summary>
   /// This interface adds a way to add an awaitable object.
   /// Once event is invoked, the event caller may wait for all the awaitables.
   /// This way the <c>async void</c> methods can be avoided.
   /// </summary>
   /// <typeparam name="TAwaitable">The type of the awaitable.</typeparam>
   /// <remarks>
   /// Because of how <c>public async void</c> methods behave, they only make sense when they are run in UI thread or similar (otherwise, they will cause hard crash, <see href="https://msdn.microsoft.com/en-us/magazine/jj991977.aspx"/>).
   /// </remarks>
   public interface EventArgsWithAsyncContext<in TAwaitable>
   {
      /// <summary>
      /// Adds an awaitable object to the current list of awaitables of this <see cref="EventArgsWithAsyncContext{TAwaitable}"/>.
      /// </summary>
      /// <param name="awaitable">The awaitable to be added.</param>
      void AddAwaitable( TAwaitable awaitable );
   }

   /// <summary>
   /// This class restricts the type argument of <see cref="EventArgsWithAsyncContext{TAwaitable}"/> to <see cref="System.Threading.Tasks.Task"/>.
   /// </summary>
   public interface EventArgsWithAsyncContext : EventArgsWithAsyncContext<System.Threading.Tasks.Task>
   {

   }

   /// <summary>
   /// The .NET Standard library does not have Task.CompletedTask property (at least for all versions).
   /// Hence, this class provides it.
   /// </summary>
#if INTERNALIZE
   internal
#else
   public
#endif
      static class TaskUtils
   {
      /// <summary>
      /// Gets the task which is completed.
      /// </summary>
      public static Task CompletedTask { get; }

      /// <summary>
      /// Gets the task which is completed to a boolean value <c>true</c>.
      /// </summary>
      /// <value>The task which is completed to a boolean value <c>true</c>.</value>
      public static Task<Boolean> True { get; }

      /// <summary>
      /// Gets the task which is completed to a boolean value <c>false</c>.
      /// </summary>
      /// <value>The task which is completed to a boolean value <c>false</c>.</value>
      public static Task<Boolean> False { get; }

      static TaskUtils()
      {
#if NETSTANDARD1_0 || NETSTANDARD1_1 || NET45
         var src = new TaskCompletionSource<Object>();
         src.SetResult( null );
#endif
         CompletedTask =
#if NETSTANDARD1_0 || NETSTANDARD1_1 || NET45
            src.Task
#else
            TTaskExt.CompletedTask
#endif
            ;

         True = TTaskExt.FromResult( true );
         False = TTaskExt.FromResult( false );
      }

      /// <summary>
      /// This method will return <see cref="True"/> or <see cref="False"/> task, depending on given <see cref="Boolean"/> value.
      /// </summary>
      /// <param name="value">The boolean of which to return corresponding <see cref="Task{TResult}"/>.</param>
      /// <returns><see cref="True"/> if <paramref name="value"/> is <c>true</c>; <see cref="False"/> otherwise.</returns>
      public static Task<Boolean> TaskFromBoolean( Boolean value )
      {
         return value ? True : False;
      }

      // We can't put these two methods in #if , since then library targeting netstandard 1.0 and loaded in .net core app would fail with missingmethodexception

      /// <summary>
      /// Creates a new instance of <see cref="Task"/> which has already been canceled.
      /// </summary>
      /// <param name="token">The <see cref="CancellationToken"/>.</param>
      /// <returns>A new instance of <see cref="Task"/> which has already been canceled.</returns>
      /// <exception cref="ArgumentException">If <see cref="CancellationToken.IsCancellationRequested"/> of given <paramref name="token"/> returns <c>false</c>.</exception>
      /// <remarks>
      /// Due to limitations of public async API, the private state bits of returned task, on some platforms, will be slightly different than the ones returned by framework's own corresponding method.
      /// </remarks>
      public static Task FromCanceled( CancellationToken token )
      {
#if NETSTANDARD1_0 || NETSTANDARD1_1 || NET45
         if ( !token.IsCancellationRequested )
         {
            throw new ArgumentException( nameof( token ) );
         }
         return new Task( () => { }, token, TaskCreationOptions.None );
#else
         return TTaskExt.FromCanceled( token );
#endif
      }

      /// <summary>
      /// Creates a new instance of <see cref="Task{T}"/> which has already been canceled.
      /// </summary>
      /// <typeparam name="T">The type of task result.</typeparam>
      /// <param name="token">The <see cref="CancellationToken"/>.</param>
      /// <returns>A new instance of <see cref="Task{T}"/> which has already been canceled.</returns>
      /// <exception cref="ArgumentException">If <see cref="CancellationToken.IsCancellationRequested"/> of given <paramref name="token"/> returns <c>false</c>.</exception>
      /// <remarks>
      /// Due to limitations of public async API, the private state bits of returned task, on some platforms, will be slightly different than the ones returned by framework's own corresponding method.
      /// </remarks>
      public static Task<T> FromCanceled<T>( CancellationToken token )
      {
#if NETSTANDARD1_0 || NETSTANDARD1_1 || NET45

         if ( !token.IsCancellationRequested )
         {
            throw new ArgumentException( nameof( token ) );
         }
         return new Task<T>( () => default, token, TaskCreationOptions.None );
#else
         return TTaskExt.FromCanceled<T>( token );
#endif
      }

   }

   public static partial class UtilPackExtensions
   {
      /// <summary>
      /// This is helper method to return task which will throw a <see cref="TimeoutException"/> if this task has not completed in given timeout.
      /// </summary>
      /// <param name="task">This task.</param>
      /// <param name="timeout">The maximum of time to wait for this task to complete.</param>
      /// <param name="token">The <see cref="CancellationToken"/> to use.</param>
      /// <returns>A task which either completes when this task does, or throws an <see cref="TimeoutException"/>.</returns>
      /// <exception cref="TimeoutException">If given amount of time has passed before this task completes.</exception>
      /// <exception cref="OperationCanceledException">If <paramref name="token"/> gets canceled before this task completes and before timeout has expired.</exception>
#if !NET40
      [System.Runtime.CompilerServices.MethodImpl( System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining )]
#endif
      public static Task TimeoutAfter( this Task task, TimeSpan timeout, CancellationToken token )
      {
         if ( task.IsCompleted || timeout == TimeSpan.FromMilliseconds( -1 ) )
         {
            // The task is done or will never timeout anyway
            return task;
         }
         else if ( timeout == TimeSpan.Zero || token.IsCancellationRequested )
         {
            // We have already timeouted
#if NET45 || NETSTANDARD1_0 || NETSTANDARD1_1
            var tsc = new TaskCompletionSource<Boolean>();
            tsc.SetException( new TimeoutException() );
            return tsc.Task;
#else
            return TTaskExt.FromException( new TimeoutException() );
#endif
         }
         else
         {
            return task.TimeoutAfterImpl( timeout, token );
         }

      }

      private static async Task TimeoutAfterImpl( this Task task, TimeSpan timeout, CancellationToken token )
      {
         using ( var linked = CancellationTokenSource.CreateLinkedTokenSource( token ) )
         {

            if ( ReferenceEquals( task, await TTaskExt.WhenAny( task, TTaskExt.Delay( timeout, linked.Token ) ) ) )
            {
               // Task completed before the timeout, cancel timeout (thus disposing of timer)
               linked.Cancel();
               await task;
            }
            else
            {
               throw new TimeoutException();
            }
         }
      }

      /// <summary>
      /// This is helper method to return task which will throw a <see cref="TimeoutException"/> if this task has not completed in given timeout.
      /// </summary>
      /// <param name="task">This task.</param>
      /// <param name="timeout">The maximum of time to wait for this task to complete.</param>
      /// <param name="token">The <see cref="CancellationToken"/> to use.</param>
      /// <returns>A task which either completes when this task does, or throws an <see cref="TimeoutException"/>.</returns>
      /// <exception cref="TimeoutException">If given amount of time has passed before this task completes.</exception>
      /// <exception cref="OperationCanceledException">If <paramref name="token"/> gets canceled before this task completes and before timeout has expired.</exception>
#if !NET40
      [System.Runtime.CompilerServices.MethodImpl( System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining )]
#endif
      public static Task<T> TimeoutAfter<T>( this Task<T> task, TimeSpan timeout, CancellationToken token )
      {
         if ( task.IsCompleted || timeout == TimeSpan.FromMilliseconds( -1 ) )
         {
            // The task is done or will never timeout anyway
            return task;
         }
         else if ( timeout == TimeSpan.Zero || token.IsCancellationRequested )
         {
            // We have already timeouted
#if NET40 || NET45 || NETSTANDARD1_0 || NETSTANDARD1_1
            var tsc = new TaskCompletionSource<T>();
            tsc.SetException( new TimeoutException() );
            return tsc.Task;
#else
            return Task.FromException<T>( new TimeoutException() );
#endif
         }
         else
         {
            return task.TimeoutAfterImpl( timeout, token );
         }

      }

      private static async Task<T> TimeoutAfterImpl<T>( this Task<T> task, TimeSpan timeout, CancellationToken token )
      {
         using ( var linked = CancellationTokenSource.CreateLinkedTokenSource( token ) )
         {

            if ( ReferenceEquals( task, await TTaskExt.WhenAny( task, TTaskExt.Delay( timeout, linked.Token ) ) ) )
            {
               // Task completed before the timeout, cancel timeout (thus disposing of timer)
               linked.Cancel();
               return await task;
            }
            else
            {
               throw new TimeoutException();
            }
         }
      }
   }

   /// <summary>
   /// This class provides default implementation for <see cref="EventArgsWithAsyncContext{TAwaitable}"/>
   /// </summary>
   /// <typeparam name="TAwaitable">The type of the awaitable.</typeparam>
   /// <remarks>
   /// Because of how <c>public async void</c> methods behave, they only make sense when they are run in UI thread or similar (otherwise, they will cause hard crash, <see href="https://msdn.microsoft.com/en-us/magazine/jj991977.aspx"/>).
   /// </remarks>
   public class EventArgsWithAsyncContextImpl<TAwaitable> : EventArgsWithAsyncContext<TAwaitable>
   {
      private List<TAwaitable> _awaitables;

      /// <summary>
      /// Creates a new instance of <see cref="EventArgsWithAsyncContext{TAwaitable}"/>
      /// </summary>
      public EventArgsWithAsyncContextImpl()
      {
         this._awaitables = null;
      }

      /// <summary>
      /// Adds the given awaitable to the list of awaitables.
      /// </summary>
      public void AddAwaitable( TAwaitable awaitable )
      {
         if ( this._awaitables == null )
         {
            this._awaitables = new List<TAwaitable>();
         }
         this._awaitables.Add( awaitable );
      }

      /// <summary>
      /// Gets the array of awaitables added to the list of awaitables, or <c>null</c> if no awaitables have been added.
      /// </summary>
      /// <returns>The array of awaitables added to the list of awaitables, or <c>null</c> if no awaitables have been added.</returns>
      public TAwaitable[] GetAwaitableArray()
      {
         var list = this._awaitables;
         return ( list?.Count ?? 0 ) > 0 ? list.ToArray() : null;
      }
   }

   /// <summary>
   /// This class restricts the type argument of <see cref="EventArgsWithAsyncContextImpl{TAwaitable}"/> to <see cref="Task"/>.
   /// </summary>
   public class EventArgsWithAsyncContextImpl : EventArgsWithAsyncContextImpl<Task>, EventArgsWithAsyncContext
   {

   }

   /// <summary>
   /// This interface extends <see cref="IAsyncDisposable"/> to provide a dispose method which accepts <see cref="CancellationToken"/>.
   /// </summary>
   public interface IAsyncDisposableWithToken : IAsyncDisposable
   {
      /// <summary>
      /// Performs the disposing of some resource asynchrnonously, with given optional <see cref="CancellationToken"/>.
      /// </summary>
      /// <param name="token">The <see cref="CancellationToken"/> to use.</param>
      /// <returns>The task to await for.</returns>
      Task DisposeAsync( CancellationToken token );
   }

   /// <summary>
   /// This is utility class to implement lock-statement-semantics using await keyword.
   /// </summary>
   public class AsyncLock
   {
      /// <summary>
      /// This struct represents lock usage scope. Acquiring it via <see cref="AsyncLock.LockAsync"/> will take the lock, and disposing it via <see cref="Dispose"/> will release the lock.
      /// </summary>
      public struct LockUseScope : IDisposable
      {
         private readonly AsyncLock _lock;

         internal LockUseScope( AsyncLock theLock )
         {
            this._lock = ArgumentValidator.ValidateNotNull( nameof( theLock ), theLock );
         }

         /// <summary>
         /// Implements <see cref="IDisposable.Dispose"/> and releases the <see cref="AsyncLock"/> this <see cref="LockUseScope"/> is associated with.
         /// </summary>
         public void Dispose()
         {
            this._lock?._semaphore?.Release();
         }
      }

      private readonly SemaphoreSlim _semaphore;
      private readonly Task<LockUseScope> _completed;
      private readonly Task<LockUseScope?> _completedNullable;
      private readonly Task<LockUseScope?> _notCompletedNullable;

      /// <summary>
      /// Creates a new instance of <see cref="AsyncLock"/>.
      /// </summary>
      public AsyncLock()
      {
         this._semaphore = new SemaphoreSlim( 1, 1 );
         this._completed = TTaskExt.FromResult( new LockUseScope( this ) );
         this._completedNullable = TTaskExt.FromResult<LockUseScope?>( new LockUseScope( this ) );
         this._notCompletedNullable = TTaskExt.FromResult<LockUseScope?>( null );
      }

      /// <summary>
      /// Tries to asynchronously acquire this <see cref="AsyncLock"/>.
      /// </summary>
      /// <param name="timeout">The maximum amout of time to wait for the lock.</param>
      /// <param name="token">The optional cancellation token to use.</param>
      /// <returns></returns>
      public Task<LockUseScope?> TryLockAsync( TimeSpan timeout, CancellationToken token = default )
      {
         var task = this._semaphore.WaitAsync( timeout, token );
         if ( task.IsCompleted )
         {
            if ( task.Status == TaskStatus.RanToCompletion )
            {
               return task.Result ? this._completedNullable : this._notCompletedNullable;
            }
            else
            {
               throw ( task.IsCanceled ? new OperationCanceledException() : task.Exception ?? new Exception( "Something went wrong when waiting for semaphore" ) );
            }
         }
         else
         {
            return task.ContinueWith<LockUseScope?>(
               ( t
#if !NET40
               , state
#endif
               ) =>
               {
                  if ( t.Status == TaskStatus.RanToCompletion )
                  {
                     if ( t.Result )
                     {
                        return new LockUseScope(
#if NET40
                     this
#else
                     (AsyncLock) state
#endif
                     );
                     }
                     else
                     {
                        return null;
                     }
                  }
                  else
                  {
                     throw ( t.IsCanceled ? new OperationCanceledException() : t.Exception ?? new Exception( "Something went wrong when waiting for semaphore" ) );
                  }
               },
#if !NET40
               this,
#endif
               TaskContinuationOptions.ExecuteSynchronously );
         }
      }

      /// <summary>
      /// Asynchronously acquires this <see cref="AsyncLock"/> or throws an exception.
      /// </summary>
      /// <returns>Potentially asynchronously returns <see cref="LockUseScope"/> that should be disposed of when this <see cref="AsyncLock"/> should be released.</returns>
      public Task<LockUseScope> LockAsync()
      {
         var task = this._semaphore.WaitAsync();
         if ( task.IsCompleted )
         {
            if ( task.Status == TaskStatus.RanToCompletion )
            {
               return this._completed;
            }
            else
            {
               throw ( task.IsCanceled ? new OperationCanceledException() : task.Exception ?? new Exception( "Something went wrong when waiting for semaphore" ) );
            }
         }
         else
         {
            return task.ContinueWith(
               ( t
#if !NET40
               , state
#endif
               ) =>
            {
               if ( t.Status == TaskStatus.RanToCompletion )
               {
                  return new LockUseScope(
#if NET40
                     this
#else
                     (AsyncLock) state
#endif
                     );
               }
               else
               {
                  throw ( t.IsCanceled ? new OperationCanceledException() : t.Exception ?? new Exception( "Something went wrong when waiting for semaphore" ) );
               }
            },
#if !NET40
               this,
#endif
               TaskContinuationOptions.ExecuteSynchronously );
         }
      }



   }
}

namespace System
{
   /// <summary>
   /// This interface provides a method which performs disposing of some resource asynchronously.
   /// </summary>
   public interface IAsyncDisposable
   {
      /// <summary>
      /// Performs the disposing of some resource asynchrnonously
      /// </summary>
      /// <returns>The task to await for.</returns>
      Task DisposeAsync();
   }


}


public static partial class E_UtilPack
{
   /// <summary>
   /// Helper method to invoke the event and then wait for any awaitables stored to the list of <see cref="EventArgsWithAsyncContextImpl"/>.
   /// </summary>
   /// <typeparam name="TEventArgs">The declared type of event args, must implement <see cref="EventArgsWithAsyncContext"/>.</typeparam>
   /// <typeparam name="TActualEventArgs">The actual type of event args, must inherit <see cref="EventArgsWithAsyncContextImpl"/> and <typeparamref name="TEventArgs"/>.</typeparam>
   /// <param name="evt">The event, may be <c>null</c>.</param>
   /// <param name="args">The event arguments to be passed to event.</param>
   /// <returns>The task to await for.</returns>
#if !NET40
   [System.Runtime.CompilerServices.MethodImpl( System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining )]
#endif
   public static async Task InvokeAndWaitForAwaitables<TEventArgs, TActualEventArgs>( this GenericEventHandler<TEventArgs> evt, TActualEventArgs args )
      where TEventArgs : EventArgsWithAsyncContext
      where TActualEventArgs : EventArgsWithAsyncContextImpl, TEventArgs
   {
      if ( evt != null )
      {
         evt( args );
         System.Threading.Tasks.Task[] awaitables;
         if ( ( awaitables = args?.GetAwaitableArray() ) != null )
         {

            await TTaskExt.WhenAll( awaitables );
         }
      }
   }

   /// <summary>
   /// Helper method to call <see cref="IAsyncDisposable.DisposeAsync"/> method and ignore any exception thrown.
   /// </summary>
   /// <param name="disposable">The <see cref="IAsyncDisposable"/>. May be <c>null</c>.</param>
   /// <returns>The task to await for <see cref="IAsyncDisposable.DisposeAsync"/> to end.</returns>
   public static async System.Threading.Tasks.Task DisposeAsyncSafely( this IAsyncDisposable disposable )
   {
      if ( disposable != null )
      {
         try
         {
            await ( disposable.DisposeAsync() ?? TaskUtils.CompletedTask );
         }
         catch
         {
            // Ignore
         }
      }
   }

   /// <summary>
   /// Helper method to call <see cref="IAsyncDisposableWithToken.DisposeAsync"/> method and ignore any exception thrown.
   /// </summary>
   /// <param name="disposable">The <see cref="IAsyncDisposable"/>. May be <c>null</c>.</param>
   /// <param name="token">The <see cref="CancellationToken"/> to use.</param>
   /// <returns>The task to await for <see cref="IAsyncDisposable.DisposeAsync"/> to end.</returns>
   public static async Task DisposeAsyncSafely( this IAsyncDisposableWithToken disposable, CancellationToken token )
   {
      if ( disposable != null )
      {
         try
         {
            await ( disposable.DisposeAsync( token ) ?? TaskUtils.CompletedTask );
         }
         catch
         {
            // Ignore
         }
      }
   }
}
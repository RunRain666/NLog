// 
// Copyright (c) 2004-2016 Jaroslaw Kowalski <jaak@jkowalski.net>, Kim Christensen, Julian Verdurmen
// 
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without 
// modification, are permitted provided that the following conditions 
// are met:
// 
// * Redistributions of source code must retain the above copyright notice, 
//   this list of conditions and the following disclaimer. 
// 
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution. 
// 
// * Neither the name of Jaroslaw Kowalski nor the names of its 
//   contributors may be used to endorse or promote products derived from this
//   software without specific prior written permission. 
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE 
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE 
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE 
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR 
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN 
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF 
// THE POSSIBILITY OF SUCH DAMAGE.
// 

#if !SILVERLIGHT && !__ANDROID__ && !__IOS__
// Unfortunately, Xamarin Android and Xamarin iOS don't support mutexes (see https://github.com/mono/mono/blob/3a9e18e5405b5772be88bfc45739d6a350560111/mcs/class/corlib/System.Threading/Mutex.cs#L167) 
#define SupportsMutex
#endif

namespace NLog.Internal.FileAppenders
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Security;
    using System.Threading;
    using System.Text;

    using NLog.Common;
    using NLog.Internal;

#if SupportsMutex
    using System.Security.AccessControl;
    using System.Security.Principal;
    using System.Security.Cryptography;
#endif

    /// <summary>
    /// Base class for optimized file appenders.
    /// </summary>
    [SecuritySafeCritical]
    internal abstract class BaseFileAppender : IDisposable
    {
        private readonly Random random = new Random();

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseFileAppender" /> class.
        /// </summary>
        /// <param name="fileName">Name of the file.</param>
        /// <param name="createParameters">The create parameters.</param>
        public BaseFileAppender(string fileName, ICreateFileParameters createParameters)
        {
            this.CreateFileParameters = createParameters;
            this.FileName = fileName;
            this.OpenTime = DateTime.UtcNow; // to be consistent with timeToKill in FileTarget.AutoClosingTimerCallback
            this.LastWriteTime = DateTime.MinValue;
            this.CaptureLastWriteTime = createParameters.CaptureLastWriteTime;
#if SupportsMutex
           
            this.ArchiveMutex = CreateArchiveMutex();
#endif
        }

        protected bool CaptureLastWriteTime { get; private set; }

        /// <summary>
        /// Gets the path of the file, including file extension.
        /// </summary>
        /// <value>The name of the file.</value>
        public string FileName { get; private set; }

        /// <summary>
        /// Gets or sets the creation time for a file associated with the appender. The time returned is in Coordinated  
        /// Universal Time [UTC] standard.
        /// </summary>
        /// <returns>The creation time of the file.</returns>
        public DateTime CreationTime { get; internal set; }

        /// <summary>
        /// Gets the last time the file associated with the appeander is opened. The time returned is in Coordinated 
        /// Universal Time [UTC] standard.
        /// </summary>
        /// <returns>The time the file was last opened.</returns>
        public DateTime OpenTime { get; private set; }

        /// <summary>
        /// Gets the last time the file associated with the appeander is written. The time returned is in  
        /// Coordinated Universal Time [UTC] standard.
        /// </summary>
        /// <returns>The time the file was last written to.</returns>
        public DateTime LastWriteTime { get; private set; }

        /// <summary>
        /// Gets the file creation parameters.
        /// </summary>
        /// <value>The file creation parameters.</value>
        public ICreateFileParameters CreateFileParameters { get; private set; }

#if !SILVERLIGHT
        /// <summary>
        /// Gets the mutually-exclusive lock for archiving files.
        /// </summary>
        /// <value>The mutex for archiving.</value>
        public Mutex ArchiveMutex { get; private set; }
#endif

        /// <summary>
        /// Writes the specified bytes.
        /// </summary>
        /// <param name="bytes">The bytes.</param>
        public abstract void Write(byte[] bytes);

        /// <summary>
        /// Flushes this instance.
        /// </summary>
        public abstract void Flush();

        /// <summary>
        /// Closes this instance.
        /// </summary>
        public abstract void Close();

        /// <summary>
        /// Gets the creation time for a file associated with the appender. The time returned is in Coordinated Universal 
        /// Time [UTC] standard.
        /// </summary>
        /// <returns>The file creation time.</returns>
        public abstract DateTime? GetFileCreationTimeUtc();

        /// <summary>
        /// Gets the last time the file associated with the appeander is written. The time returned is in Coordinated 
        /// Universal Time [UTC] standard.
        /// </summary>
        /// <returns>The time the file was last written to.</returns>
        public abstract DateTime? GetFileLastWriteTimeUtc();

        /// <summary>
        /// Gets the length in bytes of the file associated with the appeander.
        /// </summary>
        /// <returns>A long value representing the length of the file in bytes.</returns>
        public abstract long? GetFileLength();

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing">True to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.Close();
            }
        }

        /// <summary>
        /// Updates the last write time of the file.
        /// </summary>
        protected void FileTouched()
        {
            if (CaptureLastWriteTime)
            {
                FileTouched(DateTime.UtcNow);
            }
        }

        /// <summary>
        /// Updates the last write time of the file to the specified date.
        /// </summary>
        /// <param name="dateTime">Date and time when the last write occurred in UTC.</param>
        protected void FileTouched(DateTime dateTime)
        {
            this.LastWriteTime = dateTime;
        }

#if SupportsMutex
        /// <summary>
        /// Creates a mutually-exclusive lock for archiving files.
        /// </summary>
        /// <returns>A <see cref="Mutex"/> object which can be used for controlling the archiving of files.</returns>
        protected virtual Mutex CreateArchiveMutex()
        {
            return new Mutex();
        }

        /// <summary>
        /// Creates a mutex for archiving that is sharable by more than one process.
        /// </summary>
        /// <returns>A <see cref="Mutex"/> object which can be used for controlling the archiving of files.</returns>
        protected Mutex CreateSharableArchiveMutex()
        {
            return CreateSharableMutex("FileArchiveLock");
        }

        /// <summary>
        /// Creates a mutex that is sharable by more than one process.
        /// </summary>
        /// <param name="mutexNamePrefix">The prefix to use for the name of the mutex.</param>
        /// <returns>A <see cref="Mutex"/> object which is sharable by multiple processes.</returns>
        protected Mutex CreateSharableMutex(string mutexNamePrefix)
        {
            // Creates a mutex sharable by more than one process
            var mutexSecurity = new MutexSecurity();
            var everyoneSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            mutexSecurity.AddAccessRule(new MutexAccessRule(everyoneSid, MutexRights.FullControl, AccessControlType.Allow));

            // The constructor will either create new mutex or open
            // an existing one, in a thread-safe manner
            bool createdNew;
            return new Mutex(false, GetMutexName(mutexNamePrefix), out createdNew, mutexSecurity);
        }

        private string GetMutexName(string mutexNamePrefix)
        {
            const string mutexNameFormatString = @"Global\NLog-File{0}-{1}";
            const int maxMutexNameLength = 260;

            string canonicalName = Path.GetFullPath(FileName).ToLowerInvariant();

            // Mutex names must not contain a backslash, it's the namespace separator,
            // but all other are OK
            canonicalName = canonicalName.Replace('\\', '/');
            string mutexName = string.Format(mutexNameFormatString, mutexNamePrefix, canonicalName);

            // A mutex name must not exceed MAX_PATH (260) characters
            if (mutexName.Length <= maxMutexNameLength)
            {
                return mutexName;
            }

            // The unusual case of the path being too long; let's hash the canonical name,
            // so it can be safely shortened and still remain unique
            string hash;
            using (MD5 md5 = MD5.Create())
            {
                byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(canonicalName));
                hash = Convert.ToBase64String(bytes);
            }

            // The hash makes the name unique, but also add the end of the path,
            // so the end of the name tells us which file it is (for debugging)
            mutexName = string.Format(mutexNameFormatString, mutexNamePrefix, hash);
            int cutOffIndex = canonicalName.Length - (maxMutexNameLength - mutexName.Length);
            return mutexName + canonicalName.Substring(cutOffIndex);
        }
#endif

        /// <summary>
        /// Creates the file stream.
        /// </summary>
        /// <param name="allowFileSharedWriting">If set to <c>true</c> sets the file stream to allow shared writing.</param>
        /// <returns>A <see cref="FileStream"/> object which can be used to write to the file.</returns>
        protected FileStream CreateFileStream(bool allowFileSharedWriting)
        {
            int currentDelay = this.CreateFileParameters.ConcurrentWriteAttemptDelay;

            InternalLogger.Trace("Opening {0} with allowFileSharedWriting={1}", this.FileName, allowFileSharedWriting);
            for (int i = 0; i < this.CreateFileParameters.ConcurrentWriteAttempts; ++i)
            {
                try
                {
                    try
                    {
                        return this.TryCreateFileStream(allowFileSharedWriting);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        //we don't check the directory on beforehand, as that will really slow down writing.
                        if (!this.CreateFileParameters.CreateDirs)
                        {
                            throw;
                        }
                        var directoryName = Path.GetDirectoryName(this.FileName);
                        try
                        {
                            Directory.CreateDirectory(directoryName);
                        }
                        catch (DirectoryNotFoundException)
                        {
                            //if creating a directory failed, don't retry for this message (e.g the ConcurrentWriteAttempts below)
                            throw new NLogRuntimeException("Could not create directory {0}", directoryName);
                        }
                        return this.TryCreateFileStream(allowFileSharedWriting);

                    }
                }
                catch (IOException)
                {
                    if (!this.CreateFileParameters.ConcurrentWrites || i + 1 == this.CreateFileParameters.ConcurrentWriteAttempts)
                    {
                        throw; // rethrow
                    }

                    int actualDelay = this.random.Next(currentDelay);
                    InternalLogger.Warn("Attempt #{0} to open {1} failed. Sleeping for {2}ms", i, this.FileName, actualDelay);
                    currentDelay *= 2;
                    System.Threading.Thread.Sleep(actualDelay);
                }
            }

            throw new InvalidOperationException("Should not be reached.");
        }

#if !SILVERLIGHT && !MONO && !__IOS__ && !__ANDROID__
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Objects are disposed elsewhere")]
        private FileStream WindowsCreateFile(string fileName, bool allowFileSharedWriting)
        {
            int fileShare = Win32FileNativeMethods.FILE_SHARE_READ;

            if (allowFileSharedWriting)
            {
                fileShare |= Win32FileNativeMethods.FILE_SHARE_WRITE;
            }

            if (this.CreateFileParameters.EnableFileDelete && PlatformDetector.CurrentOS != RuntimeOS.Windows)
            {
                fileShare |= Win32FileNativeMethods.FILE_SHARE_DELETE;
            }

            Microsoft.Win32.SafeHandles.SafeFileHandle handle = null;
            FileStream fileStream = null;

            try
            {
                handle = Win32FileNativeMethods.CreateFile(
                fileName,
                Win32FileNativeMethods.FileAccess.GenericWrite,
                fileShare,
                IntPtr.Zero,
                Win32FileNativeMethods.CreationDisposition.OpenAlways,
                this.CreateFileParameters.FileAttributes,
                IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                }

                fileStream = new FileStream(handle, FileAccess.Write, this.CreateFileParameters.BufferSize);
                fileStream.Seek(0, SeekOrigin.End);
                return fileStream;
            }
            catch
            {
                if (fileStream != null)
                    fileStream.Dispose();

                if ((handle != null) && (!handle.IsClosed))
                    handle.Close();

                throw;
            }
        }
#endif

        private FileStream TryCreateFileStream(bool allowFileSharedWriting)
        {
            UpdateCreationTime();

#if !SILVERLIGHT && !MONO && !__IOS__ && !__ANDROID__
            try
            {
                if (!this.CreateFileParameters.ForceManaged && PlatformDetector.IsDesktopWin32)
                {
                    return this.WindowsCreateFile(this.FileName, allowFileSharedWriting);
                }
            }
            catch (SecurityException)
            {
                InternalLogger.Debug("Could not use native Windows create file, falling back to managed filestream");
            }
#endif

            FileShare fileShare = allowFileSharedWriting ? FileShare.ReadWrite : FileShare.Read;
            if (this.CreateFileParameters.EnableFileDelete && PlatformDetector.CurrentOS != RuntimeOS.Windows)
            {
                fileShare |= FileShare.Delete;
            }

            return new FileStream(
                this.FileName,
                FileMode.Append,
                FileAccess.Write,
                fileShare,
                this.CreateFileParameters.BufferSize);
        }

        private void UpdateCreationTime()
        {
            if (File.Exists(this.FileName))
            {
#if !SILVERLIGHT
                this.CreationTime = File.GetCreationTimeUtc(this.FileName);
#else
                this.CreationTime = File.GetCreationTime(this.FileName);
#endif
            }
            else
            {
                File.Create(this.FileName).Dispose();

#if !SILVERLIGHT
                this.CreationTime = DateTime.UtcNow;
                // Set the file's creation time to avoid being thwarted by Windows' Tunneling capabilities (https://support.microsoft.com/en-us/kb/172190).
                File.SetCreationTimeUtc(this.FileName, this.CreationTime);
#else
                this.CreationTime = File.GetCreationTime(this.FileName);
#endif
            }
        }
    }
}
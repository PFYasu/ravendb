﻿using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FastTests;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide.Operations;
using SlowTests.Core.Utils.Entities;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_16659 :RavenTestBase
    {
        private readonly int _reasonableTimeout = Debugger.IsAttached ? 60000 : 30000;

        public RavenDB_16659(ITestOutputHelper output) : base(output)
        {
        }
        [Fact]
        public async Task DeleteDatabaseDuringRestore()
        {
            var mre = new ManualResetEventSlim();
            var backupPath = NewDataPath();
            
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Toli" },"users/1");
                    await session.SaveChangesAsync();
                }

                var operation = await store.Maintenance.SendAsync(new BackupOperation(new BackupConfiguration
                {
                    BackupType = BackupType.Backup,
                    LocalSettings = new LocalSettings
                    {
                        FolderPath = backupPath
                    }
                }));

                var result = (BackupResult)await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

                var databaseName = $"{store}_Restore";
                try
                {
                    RestoreBackupOperation restoreOperation =
                        new RestoreBackupOperation(new RestoreBackupConfiguration
                            {BackupLocation = Path.Combine(backupPath, result.LocalBackup.BackupDirectory), DatabaseName = databaseName });
                    Server.ServerStore.ForTestingPurposesOnly().AfterSavingDatabaseRecored += () => mre.Set();
                    
                    var op  = await store.Maintenance.Server.SendAsync(restoreOperation);
                    mre.Wait();
                    
                    var e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(new DeleteDatabasesOperation(databaseName, hardDelete: true)));
                    Assert.Contains($"Can't delete database {databaseName} while restore is in progress.", e.Message);
                    await op.WaitForCompletionAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    store.Maintenance.Server.Send(new DeleteDatabasesOperation(databaseName, hardDelete: true));
                }
            }
        }
    }
}

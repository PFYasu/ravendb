﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Validators;
using Corax;
using Corax.Queries;
using Raven.Server.Documents.Queries.Parser;
using Sparrow.Server;
using Sparrow.Threading;
using static Corax.Queries.SortingMatch;

namespace Voron.Benchmark.Corax
{

    //[DisassemblyDiagnoser]
    //[InliningDiagnoser(logFailuresOnly: true, filterByNamespace: true)]
    public class OrderByBenchmark
    {
        protected StorageEnvironment Env;
        public virtual bool DeleteBeforeSuite { get; protected set; } = true;
        public virtual bool DeleteAfterSuite { get; protected set; } = true;
        public virtual bool DeleteBeforeEachBenchmark { get; protected set; } = false;


        [Params(1024, 2048, 4096, 16 * 1024)]
        //[Params(1024)]
        public int BufferSize { get; set; }

        [Params(16, 64, 256)]
        public int TakeSize { get; set; }


        /// <summary>
        /// Path to store the benchmark database into.
        /// </summary>
        public const string Path = Configuration.Path;

        /// <summary>
        /// This is the job configuration for storage benchmarks. Changing this
        /// will affect all benchmarks done.
        /// </summary>
        private class Config : ManualConfig
        {
            public Config()
            {
                AddJob(new Job
                {
                    Environment =
                    {
                        Runtime = CoreRuntime.Core50,
                        Platform = BenchmarkDotNet.Environments.Platform.X64,
                        Jit = Jit.RyuJit
                    },
                    Run =
                    {
                        LaunchCount = 1,
                        WarmupCount = 1,
                        IterationCount = 1,
                        InvocationCount = 1,
                        UnrollFactor = 1
                    },
                    // TODO: Next line is just for testing. Fine tune parameters.
                });

                // Exporters for data
                AddExporter(GetExporters().ToArray());
                // Generate plots using R if %R_HOME% is correctly set
                AddExporter(RPlotExporter.Default);

                AddColumn(StatisticColumn.AllStatistics);

                AddValidator(BaselineValidator.FailOnError);
                AddValidator(JitOptimizationsValidator.FailOnError);

                AddAnalyser(EnvironmentAnalyser.Default);
            }
        }

        public OrderByBenchmark()
        {
            if (DeleteBeforeSuite)
            {
                DeleteStorage();
            }

            if (!DeleteBeforeEachBenchmark)
            {
                Env = new StorageEnvironment(StorageEnvironmentOptions.ForPath(Path));
                GenerateData(Env);
            }
        }

        ~OrderByBenchmark()
        {
            if (!DeleteBeforeEachBenchmark)
            {
                Env.Dispose();
            }

            if (DeleteAfterSuite)
            {
                DeleteStorage();
            }
        }

        private static void GenerateData(StorageEnvironment env)
        {
            using (var writer = new IndexWriter(env))
            {
                using var bsc = new ByteStringContext(SharedMultipleUseFlag.None);
                Slice.From(bsc, "Name", ByteStringType.Immutable, out var nameSlice);
                Slice.From(bsc, "Family", ByteStringType.Immutable, out var familySlice);
                Slice.From(bsc, "Age", ByteStringType.Immutable, out var ageSlice);
                Slice.From(bsc, "Type", ByteStringType.Immutable, out var typeSlice);

                Span<byte> buffer = new byte[256];
                var fields = new Dictionary<Slice, int> { [nameSlice] = 0, [familySlice] = 1, [ageSlice] = 2, [typeSlice] = 3 };

                {
                    var entryWriter = new IndexEntryWriter(buffer, fields);
                    entryWriter.Write(0, Encoding.UTF8.GetBytes("Arava"));
                    entryWriter.Write(1, Encoding.UTF8.GetBytes("Eini"));
                    entryWriter.Write(2, Encoding.UTF8.GetBytes(12L.ToString()), 12L, 12D);
                    entryWriter.Write(3, Encoding.UTF8.GetBytes("Dog"));
                    entryWriter.Finish(out var entry);

                    writer.Index("dogs/arava", entry, fields);
                }

                {
                    var entryWriter = new IndexEntryWriter(buffer, fields);
                    entryWriter.Write(0, Encoding.UTF8.GetBytes("Phoebe"));
                    entryWriter.Write(1, Encoding.UTF8.GetBytes("Eini"));
                    entryWriter.Write(2, Encoding.UTF8.GetBytes(7.ToString()), 7L, 7D);
                    entryWriter.Write(3, Encoding.UTF8.GetBytes("Dog"));
                    entryWriter.Finish(out var entry);

                    writer.Index("dogs/phoebe", entry, fields);
                }

                for (int i = 0; i < 100_000; i++)
                {
                    var entryWriter = new IndexEntryWriter(buffer, fields);
                    entryWriter.Write(0, Encoding.UTF8.GetBytes("Dog #" + i));
                    entryWriter.Write(1, Encoding.UTF8.GetBytes("families/" + (i % 1024)));
                    var age = i % 15;
                    entryWriter.Write(2, Encoding.UTF8.GetBytes(age.ToString()), age, age);
                    entryWriter.Write(3, Encoding.UTF8.GetBytes("Dog"));
                    entryWriter.Finish(out var entry);

                    writer.Index("dogs/" + i, entry, fields);
                }

                writer.Commit();
            }
        }

        [GlobalSetup]
        public virtual void Setup()
        {
            if (DeleteBeforeEachBenchmark)
            {
                DeleteStorage();
                Env = new StorageEnvironment(StorageEnvironmentOptions.ForPath(Path));
                GenerateData(Env);
            }

            var parser = new QueryParser();
            parser.Init("from Dogs where Type = 'Dog' and (Age = '1' or Age = '10' or Age = '11' or Age = '12' or Age = '13' or Age = '14' or Age = '15' or Age = '16' or Age = '17')");
            _queryOr = new QueryDefinition("Name", parser.Parse());

            parser = new QueryParser();
            parser.Init("from Dogs where Type = 'Dog' and Age in ('1', '10', '11', '12', '13', '14', '15', '16', '17')");
            _queryIn = new QueryDefinition("Name", parser.Parse());

            parser = new QueryParser();
            parser.Init("from Dogs where Type = 'Dog' and startsWith(Age, '1')");
            //_queryStartWith = new QueryDefinition("Name", parser.Parse());

            _ids = new long[BufferSize];
        }

        protected QueryDefinition _queryOr;
        protected QueryDefinition _queryIn;
        protected QueryDefinition _queryStartWith;

        [GlobalCleanup]
        public virtual void Cleanup()
        {
            if (DeleteBeforeEachBenchmark)
            {
                Env.Dispose();
            }
        }

        private void DeleteStorage()
        {
            if (!Directory.Exists(Path))
                return;

            for (var i = 0; i < 10; ++i)
            {
                try
                {
                    Directory.Delete(Path, true);
                    break;
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }
                catch (Exception)
                {
                    Thread.Sleep(20);
                }
            }
        }

        private long[] _ids;

        [Benchmark]
        public void OrderByRuntimeQuery()
        {
            using var indexSearcher = new IndexSearcher(Env);
            var typeTerm = indexSearcher.TermQuery("Type", "Dog");
            var ageTerm = indexSearcher.StartWithQuery("Age", "1");
            var andQuery = indexSearcher.And(typeTerm, ageTerm);
            var query = indexSearcher.OrderByAscending(andQuery, fieldId: 2, take: TakeSize);           

            Span<long> ids = _ids;
            while (query.Fill(ids) != 0)
                ;
        }       
    }
}

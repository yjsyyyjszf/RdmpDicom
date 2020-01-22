﻿using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using BadMedicine;
using BadMedicine.Dicom;
using NUnit.Framework;
using Rdmp.Core.DataFlowPipeline;
using Rdmp.Dicom.PipelineComponents.DicomSources;
using Tests.Common;
using Rdmp.Dicom.PipelineComponents.DicomSources.Worklists;
using ReusableLibraryCode.Progress;
using Rdmp.Core.DataFlowPipeline.Requirements;
using Rdmp.Core.DataLoad.Engine.Pipeline.Destinations;
using FAnsi;
using Rdmp.Core.Startup;
using Rdmp.Dicom.Extraction.FoDicomBased;

namespace Rdmp.Dicom.Tests.Unit
{
    public class DicomFileCollectionSourceTests : DatabaseTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void Test_SingleFile(bool expressRelative)
        {
            var db = GetCleanedServer(DatabaseType.MicrosoftSQLServer);

            var source = new DicomFileCollectionSource();
            source.FilenameField = "RelativeFileArchiveURI";

            if (expressRelative)
                source.ArchiveRoot = TestContext.CurrentContext.TestDirectory;

            var f = new FlatFileToLoad(new FileInfo(Path.Combine(TestContext.CurrentContext.TestDirectory,@"TestData/IM-0001-0013.dcm")));
            
            source.PreInitialize(new FlatFileToLoadDicomFileWorklist(f), new ThrowImmediatelyDataLoadEventListener());

            var tbl = source.GetChunk(new ThrowImmediatelyDataLoadEventListener(), new GracefulCancellationToken());
            var destination = new DataTableUploadDestination();
            
            destination.PreInitialize(db,new ThrowImmediatelyDataLoadEventListener());
            destination.AllowResizingColumnsAtUploadTime = true;
            destination.ProcessPipelineData(tbl, new ThrowImmediatelyDataLoadEventListener(), new GracefulCancellationToken());
            destination.Dispose(new ThrowImmediatelyDataLoadEventListener(), null);

            var finalTable = db.ExpectTable(destination.TargetTableName);

            using (var dt = finalTable.GetDataTable())
            {
                //should be 1 row in the final table
                Assert.AreEqual(1,dt.Rows.Count);
                
                //the path referenced should be the file read in relative/absolute format
                Assert.AreEqual(expressRelative ? "./TestData/IM-0001-0013.dcm":
                    f.File.FullName.Replace('\\','/')
                    ,dt.Rows[0]["RelativeFileArchiveURI"]);
            }

            Assert.IsTrue(finalTable.Exists());
            finalTable.Drop();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Test_ZipFile(bool expressRelative)
        {
            //get a clean database to upload to
            var db = GetCleanedServer(DatabaseType.MicrosoftSQLServer);

            //create a folder in which to generate some dicoms
            var dirToLoad = new DirectoryInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, nameof(Test_ZipFile)));

            if(dirToLoad.Exists)
                dirToLoad.Delete(true);
            
            dirToLoad.Create();

            //generate some random dicoms
            var r = new Random(999);
            DicomDataGenerator generator = new DicomDataGenerator(r,dirToLoad,"CT");
            generator.MaximumImages = 5;
            var people = new PersonCollection();
            people.GeneratePeople(1,r);
            generator.GenerateTestDataFile(people,new FileInfo("./inventory.csv"),1);

            //This generates
            // Test_ZipFile
            //      2015
            //          3
            //              18          
            //                  751140 2.25.166922918107154891877498685128076062226.dcm
            //                  751140 2.25.179610809676265137473873365625829826423.dcm
            //                  751140 2.25.201969634959506849065133495434871450465.dcm
            //                  751140 2.25.237492679533001779093365416814254319890.dcm
            //                  751140 2.25.316241631782653383510844072713132248731.dcm

            var yearDir = dirToLoad.GetDirectories().Single();
            StringAssert.IsMatch("\\d{4}",yearDir.Name);

            //zip them up
            FileInfo zip = new FileInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, nameof(Test_ZipFile) + ".zip"));Path.Combine(TestContext.CurrentContext.TestDirectory, nameof(Test_ZipFile) + ".zip");

            if(zip.Exists)
                zip.Delete();

            ZipFile.CreateFromDirectory(dirToLoad.FullName,zip.FullName);

            //tell the source to load the zip
            var f = new FlatFileToLoad(zip);

            var source = new DicomFileCollectionSource();
            source.FilenameField = "RelativeFileArchiveURI";

            if (expressRelative)
                source.ArchiveRoot = TestContext.CurrentContext.TestDirectory;

            source.PreInitialize(new FlatFileToLoadDicomFileWorklist(f), new ThrowImmediatelyDataLoadEventListener());

            var tbl = source.GetChunk(new ThrowImmediatelyDataLoadEventListener(), new GracefulCancellationToken());
            var destination = new DataTableUploadDestination();
            
            destination.PreInitialize(db,new ThrowImmediatelyDataLoadEventListener());
            destination.AllowResizingColumnsAtUploadTime = true;
            destination.ProcessPipelineData(tbl, new ThrowImmediatelyDataLoadEventListener(), new GracefulCancellationToken());
            destination.Dispose(new ThrowImmediatelyDataLoadEventListener(), null);

            var finalTable = db.ExpectTable(destination.TargetTableName);
            
            using (var dt = finalTable.GetDataTable())
            {
                //should be 5 rows in the final table (5 images)
                Assert.AreEqual(5,dt.Rows.Count);

                string pathInDbToDicomFile = (string) dt.Rows[0]["RelativeFileArchiveURI"];

                //We expect either something like:
                // E:/RdmpDicom/Rdmp.Dicom.Tests/bin/Debug/netcoreapp2.2/Test_ZipFile.zip!2015/3/18/2.25.160787663560951826149226183314694084702.dcm
                // ./Test_ZipFile.zip!2015/3/18/2.25.105592977437473375573190160334447272386.dcm

                //the path referenced should be the file read in relative/absolute format
                StringAssert.IsMatch(
                    expressRelative ? $@"./{zip.Name}![\d./]*.dcm":
                        $@"{Regex.Escape(zip.FullName.Replace('\\','/'))}![\d./]*.dcm",
                    pathInDbToDicomFile);

                StringAssert.Contains(yearDir.Name,pathInDbToDicomFile,"Expected zip file to have subdirectories and for them to be loaded correctly");

                //confirm we can read that out again
                using (var pool = new ZipPool())
                {
                    var path = new AmbiguousFilePath(TestContext.CurrentContext.TestDirectory, pathInDbToDicomFile);
                    Assert.IsNotNull(path.GetDataset(pool));
                }
            }

            Assert.IsTrue(finalTable.Exists());
            finalTable.Drop();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Test_ZipFileNotation(bool expressRelative)
        {
            //get a clean database to upload to
            var db = GetCleanedServer(DatabaseType.MicrosoftSQLServer);

            //create a folder in which to generate some dicoms
            var dirToLoad = new DirectoryInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, nameof(Test_ZipFileNotation)));

            if(dirToLoad.Exists)
                dirToLoad.Delete(true);
            
            dirToLoad.Create();

            //generate some random dicoms
            var r = new Random(999);
            DicomDataGenerator generator = new DicomDataGenerator(r,dirToLoad,"CT");
            generator.MaximumImages = 5;
            var people = new PersonCollection();
            people.GeneratePeople(1,r);
            generator.GenerateTestDataFile(people,new FileInfo("./inventory.csv"),1);

            //This generates
            // Test_ZipFile
            //      2015
            //          3
            //              18          
            //                  751140 2.25.166922918107154891877498685128076062226.dcm
            //                  751140 2.25.179610809676265137473873365625829826423.dcm
            //                  751140 2.25.201969634959506849065133495434871450465.dcm
            //                  751140 2.25.237492679533001779093365416814254319890.dcm
            //                  751140 2.25.316241631782653383510844072713132248731.dcm

            var yearDir = dirToLoad.GetDirectories().Single();
            StringAssert.IsMatch("\\d{4}",yearDir.Name);

            //should be 5 images in the zip file
            var dicomFiles = yearDir.GetFiles("*.dcm", SearchOption.AllDirectories);
            Assert.AreEqual(5,dicomFiles.Length);

            //e.g. \2015\3\18\2.25.223398837779449245317520567111874824918.dcm
            var relativePathWithinZip = dicomFiles.First().FullName.Substring(dirToLoad.FullName.Length);

            
            //zip them up
            FileInfo zip = new FileInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, nameof(Test_ZipFile) + ".zip"));Path.Combine(TestContext.CurrentContext.TestDirectory, nameof(Test_ZipFile) + ".zip");

            if(zip.Exists)
                zip.Delete();

            ZipFile.CreateFromDirectory(dirToLoad.FullName,zip.FullName);

            //e.g. E:\RdmpDicom\Rdmp.Dicom.Tests\bin\Debug\netcoreapp2.2\Test_ZipFile.zip!\2015\3\18\2.25.223398837779449245317520567111874824918.dcm
            string pathToLoad = zip.FullName + "!" + relativePathWithinZip;

            var loadMeTextFile = new FileInfo(Path.Combine(dirToLoad.FullName, "LoadMe.txt"));

            //tell the source to load the zip
            File.WriteAllText(loadMeTextFile.FullName,pathToLoad);
            
            var f = new FlatFileToLoad(loadMeTextFile);

            var source = new DicomFileCollectionSource();
            source.FilenameField = "RelativeFileArchiveURI";

            if (expressRelative)
                source.ArchiveRoot = TestContext.CurrentContext.TestDirectory;

            source.PreInitialize(new FlatFileToLoadDicomFileWorklist(f), new ThrowImmediatelyDataLoadEventListener());

            var tbl = source.GetChunk(new ThrowImmediatelyDataLoadEventListener(), new GracefulCancellationToken());
            var destination = new DataTableUploadDestination();
            
            destination.PreInitialize(db,new ThrowImmediatelyDataLoadEventListener());
            destination.AllowResizingColumnsAtUploadTime = true;
            destination.ProcessPipelineData(tbl, new ThrowImmediatelyDataLoadEventListener(), new GracefulCancellationToken());
            destination.Dispose(new ThrowImmediatelyDataLoadEventListener(), null);

            var finalTable = db.ExpectTable(destination.TargetTableName);
            
            using (var dt = finalTable.GetDataTable())
            {
                //should be only 1 row (since we told it to only load 1 line of the zip)
                Assert.AreEqual(1,dt.Rows.Count);

                string pathInDbToDicomFile = (string) dt.Rows[0]["RelativeFileArchiveURI"];

                //We expect either something like:
                // E:/RdmpDicom/Rdmp.Dicom.Tests/bin/Debug/netcoreapp2.2/Test_ZipFile.zip!2015/3/18/2.25.160787663560951826149226183314694084702.dcm
                // ./Test_ZipFile.zip!2015/3/18/2.25.105592977437473375573190160334447272386.dcm

                //the path referenced should be the file read in relative/absolute format
                StringAssert.IsMatch(
                    expressRelative ? $@"./{zip.Name}![\d./]*.dcm":
                        $@"{Regex.Escape(zip.FullName.Replace('\\','/'))}![\d./]*.dcm",
                    pathInDbToDicomFile);

                StringAssert.Contains(yearDir.Name,pathInDbToDicomFile,"Expected zip file to have subdirectories and for them to be loaded correctly");

                //confirm we can read that out again
                using (var pool = new ZipPool())
                {
                    var path = new AmbiguousFilePath(TestContext.CurrentContext.TestDirectory, pathInDbToDicomFile);
                    Assert.IsNotNull(path.GetDataset(pool));
                }
            }

            Assert.IsTrue(finalTable.Exists());
            finalTable.Drop();
        }
    }
}

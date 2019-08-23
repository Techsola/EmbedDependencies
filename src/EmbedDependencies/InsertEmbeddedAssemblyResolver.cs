﻿using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Techsola.EmbedDependencies
{
    // Template: https://sharplab.io/#v2:EYLgxg9gTgpgtADwGwBYA0AXEBLANgHwAEAmARgFgAoQgBgAJDSUBuK2h0gOgEkB5V6vUacASjABmuGGAzYIAOwHthIgK7zZAWxicAMhACGAExhQBbUkgbE6AWQhHVUqgG8qdD3QD0Xut3F0nAByAKIAKnQAwtAw7p4ADlDYAG4GGDAcVgCCAM45MJrAuACedAAiEgZOGAD6YjkQuMnY8gDmABS5+YUl+sbRGjAIGHSQg8NodF0FRcVBBtp08gswAJRxHm6UnjsM9O2pUHQ5GLALdAC8dGFQxQDiMBj1EKpQYDDTPcUAyqcwC+1ltpVuttrtNhtwZ5sAF2iczpo6NgcksnLhVgwAOyo3C4ARQ3aEbFjdLDPSGIwAMSgEE0vwRcL+C1W+KhAF9IRywR5IT46LwMAALUwAd2RsW5dESKTSGUY2TyMxK/Pkn1mzyaMHaEGAACtpCN8vITFBJhrkjAQhaNFkoK0UWtIVsCewDgYjvD/oirjd7o9nq93mqSvSvYCYCKpoqvvNtO0dLG1iDITtnQTobDPedkTj0Vjc6z03QU1DXYc6MBVOJxKZLksI3YCtAfkzNO0wAZ4gYwNgMMUQKNhWAANYwIztdotDCrLOaPQwNpCkGgosQyWr46tzjReLFMIQdqV6umFkl8EASCJUe6s3JxkPVZrUE4+9tUAMxXaIML6a5BLZnicpCkLSqk6SZHQobnL6DxPDADSBh80azFBbbBnMKx0AYyElImK6eGm4JXiEhRjiYRgBm8MCJjkABCxToYmL63LBABqBi4KoWrYTeuErMEKyTC8IzlrACFUXhZ67AA/HQfbxDAEDiO09iOFIqycOhnCwbYBjyDC8FweJ7yoe0YkvBJKz4UWA7yGiP5/oBkqgbKEFnEYCjKmU2AyHIyy3AAPIwNCTMFAB8dAkcAZFjpR7w0fRjErFJVzyA23m+Qo7rFEFpAhRwNBhe09ItK00SaF2sDPrwUBGC0HHcK08gxJE2GOuuBFSTsADaABEYSGehvUALp1oQhC9cG2DwQAOgNJxaUYuK9VJbICFyQA=

    public sealed class InsertEmbeddedAssemblyResolver : Task
    {
        [Required]
        public ITaskItem TargetAssembly { get; set; }

        public override bool Execute()
        {
            using var stream = new FileStream(TargetAssembly.ItemSpec, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            Execute(stream);
            return true;
        }

        public static void Execute(Stream stream)
        {
            var assemblyDefinition = AssemblyDefinition.ReadAssembly(stream, new ReaderParameters { ReadSymbols = false });

            CreateModuleInitializer(assemblyDefinition.MainModule);

            assemblyDefinition.Write(stream);
        }

        private static void CreateModuleInitializer(ModuleDefinition module)
        {
            var moduleType = module.GetType("<Module>");

            var moduleInitializer = new MethodDefinition(".cctor", MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            var il = moduleInitializer.Body.GetILProcessor();

            var parts = GetTargetFramework(module.Assembly)?.Split(',');
            var frameworkName = parts?[0];
            var version = parts is null ? null : Version.Parse(parts[1].Substring("Version=v".Length));

            var appDomainAssemblyScope = module.TypeSystem.CoreLibrary;
            var collectionsAssemblyScope = module.TypeSystem.CoreLibrary;

            if (frameworkName == ".NETCoreApp")
            {
                if (version < new Version(2, 0))
                    throw new NotSupportedException("Versions of .NET Core older than 2.0 are not supported.");

                appDomainAssemblyScope = GetOrAddAssemblyReference(module, "System.Runtime.Extensions");
                collectionsAssemblyScope = GetOrAddAssemblyReference(module, "System.Collections");
            }
            else if (frameworkName == ".NETStandard")
            {
                if (version < new Version(2, 0))
                    throw new NotSupportedException("Versions of .NET Standard older than 2.0 are not supported.");
            }

            var importer = new MetadataImporter(module, scopesByAssemblySpec: new Dictionary<AssemblySpec, IMetadataScope>
            {
                [AssemblySpec.CoreLibrary] = module.TypeSystem.CoreLibrary,
                [AssemblySpec.AssemblyContainingSystemAppDomain] = appDomainAssemblyScope,
                [AssemblySpec.AssemblyContainingSystemCollections] = collectionsAssemblyScope
            });

            GenerateAppDomainModuleInitializerIL(module, moduleType, il, importer);

            moduleType.Methods.Add(moduleInitializer);
        }

        private static AssemblyNameReference GetOrAddAssemblyReference(ModuleDefinition module, string assemblyName)
        {
            var reference = module.AssemblyReferences.SingleOrDefault(r => r.Name == assemblyName);
            if (reference is null)
            {
                reference = new AssemblyNameReference(assemblyName, version: null);
                module.AssemblyReferences.Add(reference);
            }

            return reference;
        }

        private static string GetTargetFramework(AssemblyDefinition assembly)
        {
            var targetFrameworkAttribute = assembly.CustomAttributes.SingleOrDefault(a => a.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute");

            return (string)targetFrameworkAttribute?.ConstructorArguments.First().Value;
        }

        private static void GenerateAppDomainModuleInitializerIL(ModuleDefinition module, TypeDefinition moduleType, ILProcessor il, MetadataImporter importer)
        {
            var dictionaryField = CreateDictionaryField(moduleType, importer);

            var assemblyResolveHandler = CreateAppDomainAssemblyResolveHandler(module, importer);
            moduleType.Methods.Add(assemblyResolveHandler);

            il.Emit(OpCodes.Call, new MethodReference(
                "get_CurrentDomain",
                returnType: importer[TypeSpecs.SystemAppDomain],
                declaringType: importer[TypeSpecs.SystemAppDomain]));

            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldftn, assemblyResolveHandler);
            il.Emit(OpCodes.Newobj, new MethodReference(
                ".ctor",
                returnType: module.TypeSystem.Void,
                declaringType: importer[TypeSpecs.SystemResolveEventHandler])
            {
                HasThis = true,
                Parameters =
                {
                    new ParameterDefinition(module.TypeSystem.Object),
                    new ParameterDefinition(module.TypeSystem.IntPtr)
                }
            });

            il.Emit(OpCodes.Callvirt, new MethodReference(
                "add_AssemblyResolve",
                returnType: module.TypeSystem.Void,
                declaringType: importer[TypeSpecs.SystemAppDomain])
            {
                HasThis = true,
                Parameters = { new ParameterDefinition(importer[TypeSpecs.SystemResolveEventHandler]) }
            });

            il.Emit(OpCodes.Ret);
        }

        private static FieldDefinition CreateDictionaryField(TypeDefinition moduleType, MetadataImporter importer)
        {
            var field = new FieldDefinition(
                "EmbeddedResourceNamesByAssemblyName",
                FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly,
                importer[TypeSpecs.SystemCollectionsGenericDictionary(TypeSpecs.SystemString, TypeSpecs.SystemString)]);

            moduleType.Fields.Add(field);
            return field;
        }

        private static MethodDefinition CreateAppDomainAssemblyResolveHandler(ModuleDefinition module, MetadataImporter importer)
        {
            var handler = new MethodDefinition("OnAssemblyResolve", MethodAttributes.Static, returnType: importer[TypeSpecs.SystemReflectionAssembly])
            {
                Parameters =
                {
                    new ParameterDefinition(module.TypeSystem.Object),
                    new ParameterDefinition(importer[TypeSpecs.SystemResolveEventArgs])
                }
            };
            var il = handler.Body.GetILProcessor();

            // TODO

            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);

            return handler;
        }
    }
}
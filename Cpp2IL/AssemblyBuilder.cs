﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cpp2IL.Metadata;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Cpp2IL
{
    internal static class AssemblyBuilder
    {
        /// <summary>
        /// Creates all the Assemblies defined in the provided metadata, along with (stub) definitions of all the types contained therein, and registers them with the resolver.
        /// </summary>
        /// <param name="metadata">The Il2Cpp metadata to extract assemblies from</param>
        /// <param name="resolver">The Assembly Resolver that assemblies are registered with, used to ensure assemblies can cross reference.</param>
        /// <param name="moduleParams">Configuration for the module creation.</param>
        /// <returns>A list of Mono.Cecil Assemblies, containing empty type definitions for each defined type.</returns>
        internal static List<AssemblyDefinition> CreateAssemblies(Il2CppMetadata metadata, RegistryAssemblyResolver resolver, ModuleParameters moduleParams)
        {
            var assemblies = new List<AssemblyDefinition>();

            foreach (var assemblyDefinition in metadata.assemblyDefinitions)
            {
                //Get the name of the assembly (= the name of the DLL without the file extension)
                var assemblyNameString = metadata.GetStringFromIndex(assemblyDefinition.nameIndex).Replace(".dll", "");

                //Build a Mono.Cecil assembly name from this name
                var asmName = new AssemblyNameDefinition(assemblyNameString, new Version("0.0.0.0"));
                Console.Write($"\t\t{assemblyNameString}...");

                //Create an empty assembly and register it
                var assembly = AssemblyDefinition.CreateAssembly(asmName, metadata.GetStringFromIndex(assemblyDefinition.nameIndex), moduleParams);
                resolver.Register(assembly);
                assemblies.Add(assembly);

                //Ensure it really _is_ empty
                var mainModule = assembly.MainModule;
                mainModule.Types.Clear();

                //Find the end index of the types belonging to this assembly (as they're all in one huge list in the metadata)
                var end = assemblyDefinition.firstTypeIndex + assemblyDefinition.typeCount;

                for (var defNumber = assemblyDefinition.firstTypeIndex; defNumber < end; defNumber++)
                {
                    //Get the metadata type info, its namespace, and name.
                    var type = metadata.typeDefs[defNumber];
                    var ns = metadata.GetStringFromIndex(type.namespaceIndex);
                    var name = metadata.GetStringFromIndex(type.nameIndex);

                    TypeDefinition definition;
                    if (type.declaringTypeIndex != -1)
                    {
                        //This is a type declared within another (inner class/type)
                        definition = SharedState.TypeDefsByAddress[defNumber];
                    }
                    else
                    {
                        //This is a new type so ensure it's registered
                        definition = new TypeDefinition(ns, name, (TypeAttributes) type.flags);
                        mainModule.Types.Add(definition);
                        SharedState.TypeDefsByAddress.Add(defNumber, definition);
                    }

                    //Ensure we include all inner types within this type.
                    for (var nestedNumber = 0; nestedNumber < type.nested_type_count; nestedNumber++)
                    {
                        //These are stored in a separate field in the metadata.
                        var nestedIndex = metadata.nestedTypeIndices[type.nestedTypesStart + nestedNumber];
                        var nested = metadata.typeDefs[nestedIndex];

                        //Create it and register.
                        var nestedDef = new TypeDefinition(metadata.GetStringFromIndex(nested.namespaceIndex),
                            metadata.GetStringFromIndex(nested.nameIndex), (TypeAttributes) nested.flags);

                        definition.NestedTypes.Add(nestedDef);
                        SharedState.TypeDefsByAddress.Add(nestedIndex, nestedDef);
                    }
                }

                Console.WriteLine("OK");
            }

            return assemblies;
        }

        public static void ConfigureHierarchy(Il2CppMetadata metadata, PE.PE theDll)
        {
            //Iterate through all types defined in the metadata
            for (var typeIndex = 0; typeIndex < metadata.typeDefs.Length; typeIndex++)
            {
                var type = metadata.typeDefs[typeIndex];
                var definition = SharedState.TypeDefsByAddress[typeIndex];

                //Resolve this type's base class and import if required.
                if (type.parentIndex >= 0)
                {
                    var parent = theDll.types[type.parentIndex];
                    var parentRef = Utils.ImportTypeInto(definition, parent, theDll, metadata);
                    definition.BaseType = parentRef;
                }

                //Resolve this type's interfaces and import each if required.
                for (var i = 0; i < type.interfaces_count; i++)
                {
                    var interfaceType = theDll.types[metadata.interfaceIndices[type.interfacesStart + i]];
                    var interfaceTypeRef = Utils.ImportTypeInto(definition, interfaceType, theDll, metadata);
                    definition.Interfaces.Add(new InterfaceImplementation(interfaceTypeRef));
                }
            }
        }

        public static List<Tuple<TypeDefinition, List<CppMethodData>>> ProcessAssemblyTypes(Il2CppMetadata metadata, PE.PE theDll, Il2CppAssemblyDefinition imageDef)
        {
            var firstTypeDefinition = SharedState.TypeDefsByAddress[imageDef.firstTypeIndex];
            var currentAssembly = firstTypeDefinition.Module.Assembly;

            //Ensure type directory exists
            Directory.CreateDirectory(Path.Combine(Path.GetFullPath("audica_shredder_out"), "types", currentAssembly.Name.Name));

            var lastTypeIndex = imageDef.firstTypeIndex + imageDef.typeCount;
            var methods = new List<Tuple<TypeDefinition, List<CppMethodData>>>();
            for (var index = imageDef.firstTypeIndex; index < lastTypeIndex; index++)
            {
                var typeDef = metadata.typeDefs[index];
                var typeDefinition = SharedState.TypeDefsByAddress[index];
                SharedState.AllTypeDefinitions.Add(typeDefinition);

                methods.Add(new Tuple<TypeDefinition, List<CppMethodData>>(typeDefinition, ProcessTypeContents(metadata, theDll, typeDef, typeDefinition)));
            }

            return methods;
        }

        private static List<CppMethodData> ProcessTypeContents(Il2CppMetadata metadata, PE.PE cppAssembly, Il2CppTypeDefinition cppTypeDefinition, TypeDefinition ilTypeDefinition)
        {
            var typeMetaText = new StringBuilder();
            typeMetaText.Append($"Type: {ilTypeDefinition.FullName}:")
                .Append("\n\tParent Classes/Interfaces:\n");

            foreach (var iface in ilTypeDefinition.Interfaces)
            {
                typeMetaText.Append($"\t\t{iface.InterfaceType.FullName}\n");
            }

            //field
            var lastFieldIdx = cppTypeDefinition.firstFieldIdx + cppTypeDefinition.field_count;
            for (var fieldIdx = cppTypeDefinition.firstFieldIdx; fieldIdx < lastFieldIdx; ++fieldIdx)
            {
                var fieldDef = metadata.fieldDefs[fieldIdx];
                var fieldType = cppAssembly.types[fieldDef.typeIndex];
                var fieldName = metadata.GetStringFromIndex(fieldDef.nameIndex);
                var fieldTypeRef = Utils.ImportTypeInto(ilTypeDefinition, fieldType, cppAssembly, metadata);

                var fieldDefinition =
                    new FieldDefinition(fieldName, (FieldAttributes) fieldType.attrs, fieldTypeRef);
                ilTypeDefinition.Fields.Add(fieldDefinition);

                //Field default values
                if (fieldDefinition.HasDefault)
                {
                    var fieldDefault = metadata.GetFieldDefaultValueFromIndex(fieldIdx);
                    if (fieldDefault != null && fieldDefault.dataIndex != -1)
                    {
                        fieldDefinition.Constant = Utils.GetDefaultValue(fieldDefault.dataIndex,
                            fieldDefault.typeIndex, metadata, cppAssembly);
                    }
                }

                typeMetaText.Append($"\n\tField: {fieldName}\n")
                    .Append(
                        $"\t\tType: {(fieldTypeRef.Namespace == "" ? "<None>" : fieldTypeRef.Namespace)}.{fieldTypeRef.Name}\n")
                    .Append($"\t\tDefault Value: {fieldDefinition.Constant}");
            }

            //Methods
            var lastMethodId = cppTypeDefinition.firstMethodId + cppTypeDefinition.method_count;
            var typeMethods = new List<CppMethodData>();
            Il2CppGenericContainer genericContainer;
            for (var methodId = cppTypeDefinition.firstMethodId; methodId < lastMethodId; ++methodId)
            {
                var methodDef = metadata.methodDefs[methodId];
                var methodReturnType = cppAssembly.types[methodDef.returnType];
                var methodName = metadata.GetStringFromIndex(methodDef.nameIndex);
                var methodDefinition = new MethodDefinition(methodName, (MethodAttributes) methodDef.flags,
                    ilTypeDefinition.Module.ImportReference(typeof(void)));

                //TODO: If audica ever updates to Unity 2019 we'll need to fix the imageindex param from 0 to the actual index, until then it's fine.
                var offsetInRam = cppAssembly.GetMethodPointer(methodDef.methodIndex, methodId, 0, methodDef.token);


                long offsetInFile = offsetInRam == 0 ? 0 : cppAssembly.MapVirtualAddressToRaw(offsetInRam);
                typeMetaText.Append($"\n\tMethod: {methodName}:\n")
                    .Append($"\t\tFile Offset 0x{offsetInFile:X8}\n")
                    .Append($"\t\tRam Offset 0x{offsetInRam:x8}\n");

                var bytes = new List<byte>();
                var offset = offsetInFile;
                while (true)
                {
                    var b = cppAssembly.raw[offset];
                    if (b == 0xCC) break;
                    bytes.Add(b);
                    offset++;
                }

                typeMetaText.Append($"\t\tMethod Length: {bytes.Count} bytes\n");

                typeMethods.Add(new CppMethodData
                {
                    MethodName = methodName,
                    MethodId = methodId,
                    MethodBytes = bytes.ToArray(),
                    MethodOffsetRam = offsetInRam
                });


                ilTypeDefinition.Methods.Add(methodDefinition);
                methodDefinition.ReturnType = Utils.ImportTypeInto(methodDefinition, methodReturnType, cppAssembly, metadata);
                if (methodDefinition.HasBody && ilTypeDefinition.BaseType?.FullName != "System.MulticastDelegate")
                {
                    var ilprocessor = methodDefinition.Body.GetILProcessor();
                    ilprocessor.Append(ilprocessor.Create(OpCodes.Nop));
                }

                SharedState.MethodsByIndex.Add(methodId, methodDefinition);
                //Method Params
                for (var paramIdx = 0; paramIdx < methodDef.parameterCount; ++paramIdx)
                {
                    var parameterDef = metadata.parameterDefs[methodDef.parameterStart + paramIdx];
                    var parameterName = metadata.GetStringFromIndex(parameterDef.nameIndex);
                    var parameterType = cppAssembly.types[parameterDef.typeIndex];
                    var parameterTypeRef = Utils.ImportTypeInto(methodDefinition, parameterType, cppAssembly, metadata);
                    var parameterDefinition = new ParameterDefinition(parameterName, (ParameterAttributes) parameterType.attrs, parameterTypeRef);
                    methodDefinition.Parameters.Add(parameterDefinition);
                    //Default values for params
                    if (parameterDefinition.HasDefault)
                    {
                        var parameterDefault = metadata.GetParameterDefaultValueFromIndex(methodDef.parameterStart + paramIdx);
                        if (parameterDefault != null && parameterDefault.dataIndex != -1)
                        {
                            parameterDefinition.Constant = Utils.GetDefaultValue(parameterDefault.dataIndex, parameterDefault.typeIndex, metadata, cppAssembly);
                        }
                    }


                    typeMetaText.Append($"\n\t\tParameter {paramIdx}:\n")
                        .Append($"\t\t\tName: {parameterName}\n")
                        .Append($"\t\t\tType: {(parameterTypeRef.Namespace == "" ? "<None>" : parameterTypeRef.Namespace)}.{parameterTypeRef.Name}\n")
                        .Append($"\t\t\tDefault Value: {parameterDefinition.Constant}");
                }

                if (methodDef.genericContainerIndex >= 0)
                {
                    genericContainer = metadata.genericContainers[methodDef.genericContainerIndex];
                    if (genericContainer.type_argc > methodDefinition.GenericParameters.Count)
                    {
                        for (var j = 0; j < genericContainer.type_argc; j++)
                        {
                            var genericParameterIndex = genericContainer.genericParameterStart + j;
                            var param = metadata.genericParameters[genericParameterIndex];
                            var genericName = metadata.GetStringFromIndex(param.nameIndex);
                            if (!SharedState.GenericParamsByIndex.TryGetValue(genericParameterIndex,
                                out var genericParameter))
                            {
                                genericParameter = new GenericParameter(genericName, methodDefinition);
                                methodDefinition.GenericParameters.Add(genericParameter);
                                SharedState.GenericParamsByIndex.Add(genericParameterIndex, genericParameter);
                            }
                            else
                            {
                                if (!methodDefinition.GenericParameters.Contains(genericParameter))
                                {
                                    methodDefinition.GenericParameters.Add(genericParameter);
                                }
                            }
                        }
                    }
                }

                SharedState.MethodsByAddress[offsetInRam] = methodDefinition;
            }

            //Properties
            var lastPropertyId = cppTypeDefinition.firstPropertyId + cppTypeDefinition.propertyCount;
            for (var propertyId = cppTypeDefinition.firstPropertyId; propertyId < lastPropertyId; ++propertyId)
            {
                var propertyDef = metadata.propertyDefs[propertyId];
                var propertyName = metadata.GetStringFromIndex(propertyDef.nameIndex);
                TypeReference propertyType = null;
                MethodDefinition getter = null;
                MethodDefinition setter = null;
                if (propertyDef.get >= 0)
                {
                    getter = SharedState.MethodsByIndex[cppTypeDefinition.firstMethodId + propertyDef.get];
                    propertyType = getter.ReturnType;
                }

                if (propertyDef.set >= 0)
                {
                    setter = SharedState.MethodsByIndex[cppTypeDefinition.firstMethodId + propertyDef.set];
                    if (propertyType == null)
                        propertyType = setter.Parameters[0].ParameterType;
                }

                var propertyDefinition = new PropertyDefinition(propertyName, (PropertyAttributes) propertyDef.attrs, propertyType)
                {
                    GetMethod = getter,
                    SetMethod = setter
                };
                ilTypeDefinition.Properties.Add(propertyDefinition);
            }

            //Events
            var lastEventId = cppTypeDefinition.firstEventId + cppTypeDefinition.eventCount;
            for (var eventId = cppTypeDefinition.firstEventId; eventId < lastEventId; ++eventId)
            {
                var eventDef = metadata.eventDefs[eventId];
                var eventName = metadata.GetStringFromIndex(eventDef.nameIndex);
                var eventType = cppAssembly.types[eventDef.typeIndex];
                var eventTypeRef = Utils.ImportTypeInto(ilTypeDefinition, eventType, cppAssembly, metadata);
                var eventDefinition = new EventDefinition(eventName, (EventAttributes) eventType.attrs, eventTypeRef);
                if (eventDef.add >= 0)
                    eventDefinition.AddMethod = SharedState.MethodsByIndex[cppTypeDefinition.firstMethodId + eventDef.add];
                if (eventDef.remove >= 0)
                    eventDefinition.RemoveMethod = SharedState.MethodsByIndex[cppTypeDefinition.firstMethodId + eventDef.remove];
                if (eventDef.raise >= 0)
                    eventDefinition.InvokeMethod = SharedState.MethodsByIndex[cppTypeDefinition.firstMethodId + eventDef.raise];
                ilTypeDefinition.Events.Add(eventDefinition);
            }

            File.WriteAllText(Path.Combine(Path.GetFullPath("audica_shredder_out"), "types", ilTypeDefinition.Module.Assembly.Name.Name, ilTypeDefinition.Name.Replace("<", "_").Replace(">", "_") + "_metadata.txt"), typeMetaText.ToString());

            if (cppTypeDefinition.genericContainerIndex < 0) return typeMethods; //Finished processing if not generic

            genericContainer = metadata.genericContainers[cppTypeDefinition.genericContainerIndex];
            if (genericContainer.type_argc <= ilTypeDefinition.GenericParameters.Count) return typeMethods; //Finished processing

            for (var i = 0; i < genericContainer.type_argc; i++)
            {
                var genericParameterIndex = genericContainer.genericParameterStart + i;
                var param = metadata.genericParameters[genericParameterIndex];
                var genericName = metadata.GetStringFromIndex(param.nameIndex);
                if (!SharedState.GenericParamsByIndex.TryGetValue(genericParameterIndex, out var genericParameter))
                {
                    genericParameter = new GenericParameter(genericName, ilTypeDefinition);
                    ilTypeDefinition.GenericParameters.Add(genericParameter);
                    SharedState.GenericParamsByIndex.Add(genericParameterIndex, genericParameter);
                }
                else
                {
                    if (ilTypeDefinition.GenericParameters.Contains(genericParameter)) continue;
                    ilTypeDefinition.GenericParameters.Add(genericParameter);
                }
            }

            return typeMethods;
        }

        internal static List<GlobalIdentifier> MapGlobalIdentifiers(Il2CppMetadata metadata, PE.PE cppAssembly)
        {
            //Classes
            var ret = metadata.metadataUsageDic[1]
                .Select(kvp => new {kvp, type = cppAssembly.types[kvp.Value]})
                .Select(t => new GlobalIdentifier
                {
                    Name = Utils.GetTypeName(metadata, cppAssembly, t.type, true),
                    Offset = cppAssembly.metadataUsages[t.kvp.Key],
                    IdentifierType = GlobalIdentifier.Type.TYPE
                }).ToList();

            //Idx 2 is exactly the same thing
            ret.AddRange(metadata.metadataUsageDic[2]
                .Select(kvp => new {kvp, type = cppAssembly.types[kvp.Value]})
                .Select(t => new GlobalIdentifier
                {
                    Name = Utils.GetTypeName(metadata, cppAssembly, t.type, true),
                    Offset = cppAssembly.metadataUsages[t.kvp.Key],
                    IdentifierType = GlobalIdentifier.Type.TYPE
                })
            );

            //Methods is idx 3
            //Don't @ me, i prefer LINQ to foreach loops.
            //But that said this could be optimised to less t-ing
            ret.AddRange(metadata.metadataUsageDic[3]
                .Select(kvp => new {kvp, method = metadata.methodDefs[kvp.Value]})
                .Select(t => new {t.kvp, t.method, type = metadata.typeDefs[t.method.declaringType]})
                .Select(t => new {t.kvp, t.method, typeName = Utils.GetTypeName(metadata, cppAssembly, t.type)})
                .Select(t => new {t.kvp, methodName = t.typeName + "." + metadata.GetStringFromIndex(t.method.nameIndex) + "()"})
                .Select(t => new GlobalIdentifier
                {
                    IdentifierType = GlobalIdentifier.Type.METHOD,
                    Name = t.methodName,
                    Offset = cppAssembly.metadataUsages[t.kvp.Key]
                })
            );

            //Fields is idx 4
            ret.AddRange(metadata.metadataUsageDic[4]
                .Select(kvp => new {kvp, fieldRef = metadata.fieldRefs[kvp.Value]})
                .Select(t => new {t.kvp, t.fieldRef, type = cppAssembly.types[t.fieldRef.typeIndex]})
                .Select(t => new {t.type, t.kvp, t.fieldRef, typeDef = metadata.typeDefs[t.type.data.classIndex]})
                .Select(t => new {t.type, t.kvp, fieldDef = metadata.fieldDefs[t.typeDef.firstFieldIdx + t.fieldRef.fieldIndex]})
                .Select(t => new {t.kvp, fieldName = Utils.GetTypeName(metadata, cppAssembly, t.type, true) + "." + metadata.GetStringFromIndex(t.fieldDef.nameIndex)})
                .Select(t => new GlobalIdentifier
                {
                    IdentifierType = GlobalIdentifier.Type.FIELD,
                    Name = t.fieldName,
                    Offset = cppAssembly.metadataUsages[t.kvp.Key]
                })
            );

            //Literals
            ret.AddRange(metadata.metadataUsageDic[5]
                .Select(kvp => new GlobalIdentifier
                {
                    IdentifierType = GlobalIdentifier.Type.LITERAL,
                    Offset = cppAssembly.metadataUsages[kvp.Key],
                    Name = metadata.GetStringFromIndex((int) kvp.Value)
                })
            );

            return ret;
        }


        internal struct GlobalIdentifier
        {
            public ulong Offset;
            public string Name;
            public Type IdentifierType;

            public enum Type
            {
                TYPE,
                METHOD,
                FIELD,
                LITERAL
            }
        }
    }
}
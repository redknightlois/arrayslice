﻿using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using Mono.Cecil.Cil;
using System.Collections.Generic;
using Corvalius.ArraySlice.Fody.Gendarme;
using System.Diagnostics;
using System.Reflection;
using System.IO;

namespace Corvalius.ArraySlice.Fody
{
    public class ModuleWeaver
    {
        // Will log an informational message to MSBuild
        public Action<string> LogInfo { get; set; }
        public Action<string> LogWarning { get; set; }
        public Action<string> LogError { get; set; }

        // An instance of Mono.Cecil.ModuleDefinition for processing
        public ModuleDefinition ModuleDefinition { get; set; }

        public IAssemblyResolver AssemblyResolver { get; set; }

        public string AddinDirectoryPath { get; set; }


        // Init logging delegates to make testing easier
        public ModuleWeaver()
        {
            LogInfo = m => { };
            LogWarning = s => { };
            LogError = s => { };
        }

        public class SliceParameters
        {
            private static Lazy<Random> generator = new Lazy<Random>(() => new Random(), true);

            public TypeDefinition Type { get; private set; }

            public TypeReference GenericArgument { get; private set; }

            public object Definition { get; private set; }

            public string SliceName { get; private set; }

            public Instruction Instruction { get; set; }

            public VariableDefinition ArrayVariable { get; set; }
            public VariableDefinition OffsetVariable { get; set; }

            public bool IsVariable
            {
                get { return Definition is VariableDefinition || Definition is VariableReference; }
            }

            public string ArrayLink
            {
                get
                {
                    return "__" + SliceName + "array" + uniqueId;
                }
            }

            public string OffsetLink
            {
                get
                {
                    return "__" + SliceName + "offset" + uniqueId;
                }
            }

            private int uniqueId;

            public SliceParameters(ParameterDefinition definition, Instruction instruction = null)
            {
                this.Definition = definition;

                SliceName = "byname" + definition.Name + "_";
                uniqueId = generator.Value.Next(10000);

                var variableSliceType = (GenericInstanceType)definition.ParameterType;
                GenericArgument = variableSliceType.GenericArguments[0];
            }

            public SliceParameters(VariableDefinition definition, Instruction instruction)
            {
                this.Definition = definition;
                SliceName = "byidx" + definition.Index + "_";
                this.Instruction = instruction;

                uniqueId = generator.Value.Next(10000);

                var variableSliceType = (GenericInstanceType)definition.VariableType;
                GenericArgument = variableSliceType.GenericArguments[0];
            }
        }

        public void Execute()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

            try
            {
                var assemblyRef = ModuleDefinition.AssemblyReferences.FirstOrDefault(x => x.Name == "Corvalius.ArraySlice");
                if (assemblyRef == null)
                    return;                   
                
                var assemblyDefinition = AssemblyResolver.Resolve(assemblyRef);
                if (assemblyDefinition == null)
                {
                    LogError("Cannot resolve the Corvalius.ArraySlice assembly.");
                    return;
                }                    

                var typeToFind = assemblyDefinition.MainModule.Types.FirstOrDefault(x => x.Name == "ArraySlice`1");
                if ( typeToFind == null )
                {
                    LogError("Cannot find an instance of ArraySlice<T> in the referenced assembly '" + assemblyDefinition.Name + "'.");
                    return;
                }

                var methodsToProcess = FindMethodsUsingArraySlices(typeToFind);
                foreach (var method in methodsToProcess)
                {
                    method.Body.SimplifyMacros();

                    var occurrences = LocateIntermediateInjectionPoints(method, typeToFind).ToList();
                    occurrences = PruneUnusedInjectionPoints(method, typeToFind, occurrences).ToList();
                    occurrences.Reverse();

                    IntroduceIntermediateVariables(method, occurrences);

                    method.Body.OptimizeMacros();
                    method.Body.SimplifyMacros();

                    ReplaceIndexersCalls(method, occurrences);

                    method.Body.OptimizeMacros();
                }

                RemoveAttributes();
            }
            catch (Exception ex)
            {
                LogError(ex.Message + Environment.NewLine + "StackTrace: " + ex.StackTrace);
#if DEBUG
                Debugger.Launch();
#endif
            }
        }

        private Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                LogInfo("Entering custom resolve assembly for '" + args.Name + "' and requested by '" + args.RequestingAssembly.FullName + "'");
                if (args.Name.Contains("Corvalius.ArraySlice") && !string.IsNullOrWhiteSpace(this.AddinDirectoryPath))
                {
                    var assemblyFilename = Path.Combine(this.AddinDirectoryPath, "Corvalius.ArraySlice.dll");
                    LogWarning("Assembly: " + assemblyFilename);

                    return Assembly.LoadFile(assemblyFilename);
                }
            }
            finally
            {
                LogInfo("Exiting custom resolve assembly");
            }

            return null;
        }

        private IEnumerable<SliceParameters> PruneUnusedInjectionPoints(MethodDefinition method, TypeDefinition typeToFind, IEnumerable<SliceParameters> occurrences)
        {
            // We filter out explicitely left out optimizations.
            CustomAttribute behavior;
            if (method.ContainsBehaviorAttribute(out behavior))
            {
                var mode = behavior.AttributeType.Name;

                switch (mode)
                {
                    case "ArraySliceDoNotOptimize": return new SliceParameters[0];
                }
            }

            return occurrences;
        }

        public void ReplaceIndexersCalls(MethodDefinition method, IList<SliceParameters> slices)
        {
            var processor = method.Body.GetILProcessor();
            var instructions = processor.Body.Instructions;

            var slicesLookup = slices.ToLookup(x => x.Definition);

            for (int offset = 0; offset < instructions.Count; offset++)
            {
                var instruction = instructions[offset];
                if (!instruction.OpCode.IsCall())
                    continue;

                var methodReference = instruction.Operand as MethodReference;
                if (methodReference == null)
                    continue;

                if (methodReference.Name != "get_Item" && methodReference.Name != "set_Item")
                    continue;                

                var objectToCall = instruction.TraceBack(methodReference, 0);
                var parameter = instruction.TraceBack(methodReference, -1);
                
                var slice = slicesLookup[objectToCall.Operand].FirstOrDefault();
                if (slice == null)
                    continue;

                var arraySliceType = ModuleDefinition.Import(typeof(ArraySlice<>)).MakeGenericInstanceType(slice.GenericArgument);

                var methodDefinition = methodReference.Resolve();

                // FIXME: We know we cannot process if anything happens between the push of the array slice and the final operation.
                if (methodDefinition.IsGetter)
                {
                    // Original GET:
                    // 1    ldarg.s segment              $ Push the object to call 
                    // =    ldloc.1                      $ Push the parameter
                    // 2    callvirt instance !0 class ArraySegment.ArraySlice`1<float32>::get_Item(int32)
                    // =    stloc.s t                    $ Do whatever with the result of the GET call

                    // Target GET:
                    // 1    ldloc.s data                 $ Push the object to call  
                    // =    ldloc.1                      $ Push the parameter
                    // 2    ldloc.s offset
                    // 2    add                          $ Add the parameter (i) and the offset. 
                    // 2    ldelem.r4                    $ Use standard ldelem with type to get the value from the array.
                    // =    stloc.s t                    $ Do whatever with the result of the GET call   

                    //HACK: We rewrite it to avoid killing the loop.
                    objectToCall.OpCode = OpCodes.Ldloc;
                    objectToCall.Operand = slice.ArrayVariable;                    

                    processor.Replace(instruction, Instruction.Create(OpCodes.Ldelem_Any, slice.GenericArgument));

                    processor.InsertAfter(parameter, new[] { 
                                Instruction.Create( OpCodes.Ldloc, slice.OffsetVariable ),
                                Instruction.Create( OpCodes.Add )                                
                        });
                }
                else if (methodDefinition.IsSetter)
                {
                    // Original SET:
                    // 1		ldarg segment	    $ Push the object to call 
                    // =		ldloc.0		        $ Push the parameter
                    // =		ldc.r4 2		    $ Push the value to set
                    // 2		callvirt instance void class ArraySegment.ArraySlice`1<float32>::set_Item(int32, !0)
                    // -        nop			        $ Check if it even exists after SimplifyMacros in the collection.

                    // Target SET
                    // 1		ldloc data		    $ Push the array
                    // =		ldloc.0		        $ Push the parameter
                    // 2		ldloc offset		
                    // 2		add      		    $ Add the parameter (i) and the offset. 
                    // =		ldc.r4 2		    $ Push the value to set. Use standard ldc with type and check if it  is optimized afterwards.
                    // 2		stelem   		    $ Use standard stelem with type and check if it is optimized afterwards.

                    //HACK: We rewrite it to avoid killing the loop.
                    objectToCall.OpCode = OpCodes.Ldloc;
                    objectToCall.Operand = slice.ArrayVariable;

                    processor.Replace(instruction, Instruction.Create(OpCodes.Stelem_Any, slice.GenericArgument));

                    processor.InsertAfter(parameter, new[] { 
                                Instruction.Create( OpCodes.Ldloc, slice.OffsetVariable ),
                                Instruction.Create( OpCodes.Add )                                
                        });

                }
                else continue;
            }

        }

        public void IntroduceIntermediateVariables(MethodDefinition method, IEnumerable<SliceParameters> slices)
        {
            var body = method.Body;
            var processor = body.GetILProcessor();           

            foreach ( var slice in slices )
            {
                var arraySliceType = ModuleDefinition.Import(typeof(ArraySlice<>)).MakeGenericInstanceType(slice.GenericArgument);
                var arrayGetMethod = ModuleDefinition.Import(arraySliceType.Resolve().FindField("Array")).MakeHostInstanceGeneric(slice.GenericArgument);
                var offsetGetMethod = ModuleDefinition.Import(arraySliceType.Resolve().FindField("Offset")).MakeHostInstanceGeneric(slice.GenericArgument);

                var arrayTypeOfT = slice.GenericArgument.MakeArrayType();

                VariableDefinition arrayVariable = new VariableDefinition(slice.ArrayLink, arrayTypeOfT);
                VariableDefinition offsetVariable = new VariableDefinition(slice.OffsetLink, ModuleDefinition.TypeSystem.Int32);

                method.Body.Variables.Add(arrayVariable);
                method.Body.Variables.Add(offsetVariable);

                var instructionsToAdd = new Instruction[] 
                {
                    slice.IsVariable ? Instruction.Create(OpCodes.Ldloc, (VariableDefinition)slice.Definition) : Instruction.Create(OpCodes.Ldarg, (ParameterDefinition)slice.Definition),
                    Instruction.Create(OpCodes.Ldfld, arrayGetMethod),
                    Instruction.Create(OpCodes.Stloc, arrayVariable),
                    slice.IsVariable ? Instruction.Create(OpCodes.Ldloc, (VariableDefinition)slice.Definition) : Instruction.Create(OpCodes.Ldarg, (ParameterDefinition)slice.Definition),
                    Instruction.Create(OpCodes.Ldfld, offsetGetMethod),
                    Instruction.Create(OpCodes.Stloc, offsetVariable)
                };

                if (slice.IsVariable)
                    processor.InsertAfter(slice.Instruction, instructionsToAdd);
                else
                    processor.Prepend(instructionsToAdd);

                slice.ArrayVariable = arrayVariable;
                slice.OffsetVariable = offsetVariable;
            }     
        }

        public IEnumerable<SliceParameters> LocateIntermediateInjectionPoints(MethodDefinition method, TypeDefinition typeToFind)
        {
            foreach (var parameter in method.Parameters)
            {
                if (parameter.ParameterType.Resolve().FullName == typeToFind.FullName)
                    yield return new SliceParameters(parameter);
            }

            var instructions = method.Body.Instructions;

            for (int i = 0; i < instructions.Count; i++)
            {
                var instruction = instructions[i];

                switch (instruction.OpCode.Code)
                {
                    case Code.Starg:
                        {
                            // Get the index.
                            var locationIndex = ((ParameterDefinition)instruction.Operand).Index;
                            var parameter = method.Parameters[locationIndex];
                            if (parameter.ParameterType.Name != typeToFind.Name)
                                continue;

                            yield return new SliceParameters(parameter, instruction);
                        }
                        break;
                    case Code.Stloc:
                        {
                            // Get the index.
                            var locationIndex = ((VariableDefinition)instruction.Operand).Index;
                            var variable = method.Body.Variables[locationIndex];
                            if (variable.VariableType.Name != typeToFind.Name)
                                continue;

                            yield return new SliceParameters(variable, instruction);
                        }
                        break;
                    default: continue;
                }
            }

            yield break;
        }

        public IEnumerable<MethodDefinition> FindMethodsUsingArraySlices(TypeDefinition typeToFind)
        {
            var methods = from type in ModuleDefinition.GetAllTypes()
                          from method in type.Methods
                          where IsMethodUsingTypeIndexers(method, typeToFind)
                          select method;

            return methods;
        }

        private bool IsMethodUsingTypeIndexers(MethodDefinition method, TypeDefinition typeToFind)
        {
            if (method.Body == null)
                return false;

            foreach (var instruction in method.Body.Instructions)
            {
                if (!instruction.OpCode.IsCall())
                    continue;

                var methodReference = instruction.Operand as MethodReference;
                if (methodReference == null)
                    continue;

                if (methodReference.Name != "get_Item" && methodReference.Name != "set_Item")
                    continue;

                var methodDefinition = methodReference.Resolve();
                if (!(methodDefinition.IsSetter || methodDefinition.IsGetter))
                    continue;

                var genericInstanceType = methodDefinition.DeclaringType;
                if (genericInstanceType.FullName == typeToFind.FullName)
                    return true;
            }

            return false;
        }

        private void RemoveAttributes()
        {
            ModuleDefinition.Assembly.RemoveAllArraySliceAttributes();
            ModuleDefinition.RemoveAllArraySliceAttributes();

            var allTypes = new List<TypeDefinition>(ModuleDefinition.GetTypes());
            foreach (var typeDefinition in allTypes)
            {
                typeDefinition.RemoveAllArraySliceAttributes();

                foreach (var method in typeDefinition.Methods)
                {
                    method.RemoveAllArraySliceAttributes();

                    foreach (var parameter in method.Parameters)
                    {
                        parameter.RemoveAllArraySliceAttributes();
                    }
                }

                foreach (var property in typeDefinition.Properties)
                {
                    property.RemoveAllArraySliceAttributes();
                }
            }
        }
    }
}
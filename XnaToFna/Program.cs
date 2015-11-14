using System;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace XnaToFna {
    static class Program {

        private static ModuleDefinition FNA;
        
        private static ModuleDefinition Module;
        
        private static TypeReference ImportIfNeeded(this ModuleDefinition module, TypeReference r) {
            return r == null ? null : r.Module.Name != module.Name ? module.Import(r) : r;
        }
        private static MethodReference ImportIfNeeded(this ModuleDefinition module, MethodReference r) {
            return r == null ? null : r.Module.Name != module.Name ? module.Import(r) : r;
        }
        private static FieldReference ImportIfNeeded(this ModuleDefinition module, FieldReference r) {
            return r == null ? null : r.Module.Name != module.Name ? module.Import(r) : r;
        }
        
        private static bool IsXNA(this TypeReference r) {
            if (r == null) {
                return false;
            }
            if (r.GetElementType() != null && r.GetElementType() != r && r.GetElementType().IsXNA()) {
                return true;
            }
            if (r.IsGenericInstance) {
                foreach (TypeReference genericArgument in ((GenericInstanceType) r).GenericArguments) {
                    if (genericArgument.IsXNA()) {
                        return true;
                    }
                }
            }
            return r.Scope.Name.Contains("Microsoft.Xna.Framework") || r.Scope.Name.Contains("FNA");
        }
        private static bool IsXNA(this MethodReference r) {
            if (r == null) {
                return false;
            }
            if (r.IsGenericInstance) {
                foreach (TypeReference genericArgument in ((GenericInstanceMethod) r).GenericArguments) {
                    if (genericArgument.IsXNA()) {
                        return true;
                    }
                }
            }
            for (int i = 0; i < r.Parameters.Count; i++) {
                if (r.Parameters[i].ParameterType.IsXNA()) {
                    return true;
                }
            }
            return r.DeclaringType.IsXNA() || r.ReturnType.IsXNA();
        }
        private static bool IsXNA(this FieldReference r) {
            if (r == null) {
                return false;
            }
            return r.DeclaringType.IsXNA() || r.FieldType.IsXNA();
        }
        
        private static string ReplaceGenerics(string str, MethodReference method, TypeReference type) {
            //FNA is weird... or Cecil
            /*if (!type.HasGenericParameters) {
                return str;
            }*/
            for (int i = 0; i < type.GenericParameters.Count; i++) {
                str = str.Replace(type.GenericParameters[i].Name, "!"+i);
            }
            /*for (int i = 0; i < method.GenericParameters.Count; i++) {
                str = str.Replace(method.GenericParameters[i].Name, "!!"+i);
            }*/
            //screw this - remove all generic stuff!
            int genOpen = str.IndexOf("<", str.IndexOf("::"));
            if (genOpen > -1) {
                //let's just pretend generic in generics don't exist
                int genClose = str.IndexOf(">", genOpen);
                str = str.Substring(0, genOpen) + str.Substring(genClose + 1);
            }
            return str;
        }

        private static TypeReference FindFNA(this TypeReference type, MemberReference context = null) {
            if (type == null) {
                Console.WriteLine("Can't find null type! Context: " + (context == null ? "none" : context.FullName));
                Console.WriteLine(Environment.StackTrace);
                return null;
            }
            TypeReference foundType = type.IsXNA() ? FNA.GetType(type.FullName) : null;
            if (foundType == null && type.IsByReference) {
                foundType = type.GetElementType().FindFNA(context);
            }
            if (foundType == null && type.IsArray) {
                foundType = new ArrayType(Module.ImportIfNeeded(type.GetElementType().FindFNA(context)));
            }
            if (foundType == null && context != null && type.IsGenericParameter) {
                foundType = type.FindFNAGeneric(context); 
            }
            if (foundType == null && context != null && type.IsGenericInstance) {
                foundType = new GenericInstanceType(Module.ImportIfNeeded(type.GetElementType().FindFNA(context)));
                foreach (TypeReference genericArgument in ((GenericInstanceType) type).GenericArguments) {
                    ((GenericInstanceType) foundType).GenericArguments.Add(Module.ImportIfNeeded(genericArgument.FindFNA(context)));
                }
            }
            if (type.IsXNA() && foundType == null) {
                Console.WriteLine("Could not find type " + type.FullName);
            }
            return (type.IsXNA() ? Module.ImportIfNeeded(foundType) : null) ?? type;
        }

        private static TypeReference FindFNAGeneric(this TypeReference type, MemberReference context) {
            if (context is MethodReference) {
                for (int gi = 0; gi < ((MethodReference) context).GenericParameters.Count; gi++) {
                    GenericParameter genericParam = ((MethodReference) context).GenericParameters[gi];
                    if (genericParam.FullName == type.FullName) {
                        return genericParam;
                    }
                }
            }
            if (context is TypeReference) {
                for (int gi = 0; gi < ((TypeReference) context).GenericParameters.Count; gi++) {
                    GenericParameter genericParam = ((TypeReference) context).GenericParameters[gi];
                    if (genericParam.FullName == type.FullName) {
                        return genericParam;
                    }
                }
            }
            if (context.DeclaringType != null) {
                return type.FindFNAGeneric(context.DeclaringType);
            }
            return type;
        }

        private static MethodReference FindFNA(this MethodReference method, MemberReference context) {
            if (!method.IsXNA()) {
                return method;
            }
            TypeReference findTypeRef = method.DeclaringType.FindFNA(context);
            TypeDefinition findType = findTypeRef == null ? null : findTypeRef.IsDefinition ? (TypeDefinition) findTypeRef : findTypeRef.Resolve();
            
            if (findType != null) {
                string methodName = method.FullName;
                methodName = methodName.Substring(methodName.IndexOf(" ") + 1);
                methodName = ReplaceGenerics(methodName, method, findType);
                //Console.WriteLine("debug m -1 / " + (findType.Methods.Count - 1) + ": " + methodName);
                for (int ii = 0; ii < findType.Methods.Count; ii++) {
                    MethodReference foundMethod = findType.Methods[ii];
                    string foundMethodName = foundMethod.FullName;
                    foundMethodName = foundMethodName.Replace(findType.FullName, findTypeRef.FullName);
                    foundMethodName = foundMethodName.Substring(foundMethodName.IndexOf(" ") + 1);
                    //TODO find a better way to compare methods / fix comparing return types
                    foundMethodName = ReplaceGenerics(foundMethodName, foundMethod, findType);
                    //Console.WriteLine("debug m "+ii+" / " + (findType.Methods.Count - 1) + ": " + foundMethodName);

                    if (methodName == foundMethodName) {
                        foundMethod = Module.ImportIfNeeded(foundMethod);
                        
                        if (method.DeclaringType.IsGenericInstance) {
                            //TODO test return type context
                            MethodReference genMethod = new MethodReference(method.Name, FindFNA(method.ReturnType, findTypeRef), findTypeRef);
                            genMethod.CallingConvention = method.CallingConvention;
                            genMethod.HasThis = method.HasThis;
                            genMethod.ExplicitThis = method.ExplicitThis;
                            for (int i = 0; i < method.GenericParameters.Count; i++) {
                                genMethod.GenericParameters.Add((GenericParameter) FindFNA(method.GenericParameters[i], genMethod));
                            }
                            for (int i = 0; i < method.Parameters.Count; i++) {
                                genMethod.Parameters.Add(new ParameterDefinition(FindFNA(method.Parameters[i].ParameterType, genMethod)));
                            }
                            
                            foundMethod = Module.ImportIfNeeded(genMethod);
                        }
                        
                        if (method.IsGenericInstance) {
                            GenericInstanceMethod genMethod = new GenericInstanceMethod(foundMethod);
                            GenericInstanceMethod methodg = ((GenericInstanceMethod) method);
                            
                            for (int i = 0; i < methodg.GenericArguments.Count; i++) {
                                genMethod.GenericArguments.Add(FindFNA(methodg.GenericArguments[i], genMethod));
                            }
                            
                            foundMethod = genMethod;
                        }
                        
                        return foundMethod;
                    }
                }
            }

            //For anyone trying to find out why / when no method gets found: Take this!
            Console.WriteLine("debug method fullname   : " + method.FullName);
            Console.WriteLine("debug method findTypeRef: " + findTypeRef);
            Console.WriteLine("debug method findType   : " + findType);
            Console.WriteLine("debug method type scope : " + findTypeRef.Scope.Name);
            
            if (findTypeRef == null) {
                return method;
            }
            
            MethodReference fbgenMethod = new MethodReference(method.Name, FindFNA(method.ReturnType, findTypeRef), findTypeRef);
            fbgenMethod.CallingConvention = method.CallingConvention;
            fbgenMethod.HasThis = method.HasThis;
            fbgenMethod.ExplicitThis = method.ExplicitThis;
            for (int i = 0; i < method.GenericParameters.Count; i++) {
                fbgenMethod.GenericParameters.Add((GenericParameter) FindFNA(method.GenericParameters[i], fbgenMethod));
            }
            for (int i = 0; i < method.Parameters.Count; i++) {
                fbgenMethod.Parameters.Add(new ParameterDefinition(FindFNA(method.Parameters[i].ParameterType, fbgenMethod)));
            }
            
            if (method.IsGenericInstance) {
                GenericInstanceMethod genMethod = new GenericInstanceMethod(fbgenMethod);
                GenericInstanceMethod methodg = ((GenericInstanceMethod) method);
                
                for (int i = 0; i < methodg.GenericArguments.Count; i++) {
                    genMethod.GenericArguments.Add(FindFNA(methodg.GenericArguments[i], genMethod));
                }
                
                fbgenMethod = genMethod;
            }
            
            return fbgenMethod;
        }

        private static void patch(TypeDefinition type) {
            for (int i = 0; i < type.NestedTypes.Count; i++) {
                patch(type.NestedTypes[i]);
            }
            
            if (type.BaseType != null) {
                type.BaseType = type.BaseType.FindFNA(type);
            }
            
            for (int ii = 0; ii < type.Fields.Count; ii++) {
                 FieldDefinition field = type.Fields[ii];
                 if (!field.FieldType.IsXNA()) {
                    continue;
                 }
                 field.FieldType = field.FieldType.FindFNA(type);
            }
            
            for (int ii = 0; ii < type.Properties.Count; ii++) {
                PropertyDefinition property = type.Properties[ii];
                if (!property.PropertyType.IsXNA()) {
                    continue;
                }
                property.PropertyType = property.PropertyType.FindFNA(type);
            }

            for (int i = 0; i < type.Methods.Count; i++) {
                MethodDefinition method = type.Methods[i];
                Console.WriteLine("M: " + method.FullName);
                
                for (int ii = 0; method.HasBody && ii < method.Body.Variables.Count; ii++) {
                    method.Body.Variables[ii].VariableType = method.Body.Variables[ii].VariableType.FindFNA(method);
                }
                
                for (int ii = 0; ii < method.Parameters.Count; ii++) {
                    method.Parameters[ii].ParameterType = method.Parameters[ii].ParameterType.FindFNA(method);
                }
                
                method.ReturnType = method.ReturnType.FindFNA(method);
                
                for (int ii = 0; method.HasBody && ii < method.Body.Instructions.Count; ii++) {
                    Instruction instruction = method.Body.Instructions[ii];
                    
                    if (instruction.Operand is TypeReference) {
                        instruction.Operand = ((TypeReference) instruction.Operand).FindFNA(method);
                    } else if (instruction.Operand is MethodReference && ((MethodReference) instruction.Operand).IsXNA()) {
                        instruction.Operand = ((MethodReference) instruction.Operand).FindFNA(method);
                    } /*else if (instruction.Operand is MethodReference) {
                        MethodReference methodr = (MethodReference) instruction.Operand;
                        
                        MethodReference genMethod = new MethodReference(methodr.Name, methodr.ReturnType.FindFNA(method), methodr.DeclaringType.FindFNA(method));
                        genMethod.CallingConvention = methodr.CallingConvention;
                        genMethod.HasThis = methodr.HasThis;
                        genMethod.ExplicitThis = methodr.ExplicitThis;
                        for (int iii = 0; iii < methodr.GenericParameters.Count; iii++) {
                            genMethod.GenericParameters.Add((GenericParameter) methodr.Parameters[iii].ParameterType.FindFNA(genMethod));
                        }
                        for (int iii = 0; iii < methodr.Parameters.Count; iii++) {
                            genMethod.Parameters.Add(new ParameterDefinition(methodr.Parameters[iii].ParameterType.FindFNA(genMethod)));
                        }
                        
                        instruction.Operand = Module.Import(genMethod);
                    }*/ else if (instruction.Operand is FieldReference && ((FieldReference) instruction.Operand).IsXNA()) {
                        FieldReference field = (FieldReference) instruction.Operand;
    
                        TypeReference findTypeRef = field.DeclaringType.FindFNA(method);
                        TypeDefinition findType = findTypeRef == null ? null : findTypeRef.IsXNA() ? null : findTypeRef.Resolve();
                        findTypeRef = Module.ImportIfNeeded(findTypeRef);
                        
                        if (findType != null) {
                            for (int iii = 0; iii < findType.Fields.Count; iii++) {
                                if (findType.Fields[iii].Name == field.Name) {
                                    FieldReference foundField = findType.Fields[iii];
                                    
                                    if (field.DeclaringType.IsGenericInstance) {
                                        foundField = Module.ImportIfNeeded(new FieldReference(field.Name, field.FieldType.FindFNA(findTypeRef), findTypeRef));
                                    }
                                    
                                    field = Module.ImportIfNeeded(foundField);
                                    break;
                                }
                            }
                        }
    
                        if (field == instruction.Operand) {
                            field = Module.Import(new FieldReference(field.Name, Module.ImportIfNeeded(field.FieldType.FindFNA(method)), Module.ImportIfNeeded(field.DeclaringType.FindFNA(method))));
                        }
                        
                        instruction.Operand = field;
                    }
                }
            }
        }
        
        public static void Main(string[] args) {
            Console.WriteLine("Loading FNA");
            FNA = ModuleDefinition.ReadModule("FNA.dll");

            foreach (string arg in args) {
                Console.WriteLine("Patching " + arg);
                Module = ModuleDefinition.ReadModule(arg);

                for (int i = 0; i < Module.AssemblyReferences.Count; i++) {
                    if (Module.AssemblyReferences[i].Name.StartsWith("Microsoft.Xna.Framework.")) {
                        //Xact is causing some random issues
                        /*if (Module.AssemblyReferences[i].Name.EndsWith(".Xact")) {
                            continue;
                        }*/
                        Console.WriteLine("Found reference to XNA assembly " + Module.AssemblyReferences[i].Name + " - removing");
                        Module.AssemblyReferences.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (Module.AssemblyReferences[i].Name != "Microsoft.Xna.Framework") {
                        continue;
                    }
                    Console.WriteLine("Found reference to XNA - replacing with FNA");
                    Module.AssemblyReferences[i] = new AssemblyNameReference("FNA", new Version(0, 0, 0, 1));
                }
                
                for (int i = 0; i < Module.Types.Count; i++) {
                    patch(Module.Types[i]);
                }

                Module.Write(arg);
                //For those brave enough: Feel free to fix the error caused by uncommenting the following lines.
                //FIXME find the cause of the unreadwritability bug
                //Module = ModuleDefinition.ReadModule(arg);
                //Module.Write(arg);
            }
        }

    }
}

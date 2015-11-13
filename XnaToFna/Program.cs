using System;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace XnaToFna {
    static class Program {

        private static ModuleDefinition FNA;
        
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
            return r != null && r.Scope.Name.Contains("Microsoft.Xna.Framework");
        }
        private static bool IsXNA(this MethodReference r) {
            return r != null && r.DeclaringType.Scope.Name.Contains("Microsoft.Xna.Framework");
        }
        private static bool IsXNA(this FieldReference r) {
            return r != null && (r.DeclaringType.Scope.Name.Contains("Microsoft.Xna.Framework") || r.FieldType.Scope.Name.Contains("Microsoft.Xna.Framework"));
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
            if (!type.IsXNA()) {
                return type;
            }
            if (type == null) {
                Console.WriteLine("ERROR: Can't find null type!");
                Console.WriteLine(Environment.StackTrace);
                return null;
            }
            string typeName = type.FullName; //RemovePrefixes
            TypeReference foundType = FNA.GetType(typeName);
            if (foundType == null && type.IsByReference) {
                foundType = FindFNA(type.GetElementType(), context);
            }
            if (foundType == null && type.IsArray) {
                foundType = new ArrayType(FindFNA(type.GetElementType(), context));
            }
            if (foundType == null && context != null && type.IsGenericParameter) {
                foundType = FindFNAGeneric(type, context); 
           }
            if (foundType == null && context != null && type.IsGenericInstance) {
                foundType = new GenericInstanceType(FindFNA(type.GetElementType(), context));
                foreach (TypeReference genericArgument in ((GenericInstanceType) type).GenericArguments) {
                    ((GenericInstanceType) foundType).GenericArguments.Add(FindFNA(genericArgument, context));
                }
            }
            if (foundType == null) {
                Console.WriteLine("Could not find type " + type.FullName);
            }
            return foundType ?? type;
        }

        private static TypeReference FindFNAGeneric(this TypeReference type, MemberReference context) {
            if (!type.IsXNA()) {
                return type;
            }
            if (context is MethodReference) {
                for (int gi = 0; gi < ((MethodReference) context).GenericParameters.Count; gi++) {
                    GenericParameter genericParam = ((MethodReference) context).GenericParameters[gi];
                    if (genericParam.FullName == type.FullName) {
                        //TODO variables hate me, import otherwise
                        return genericParam;
                    }
                }
            }
            if (context is TypeReference) {
                for (int gi = 0; gi < ((TypeReference) context).GenericParameters.Count; gi++) {
                    GenericParameter genericParam = ((TypeReference) context).GenericParameters[gi];
                    if (genericParam.FullName == type.FullName) {
                        //TODO variables hate me, import otherwise
                        return genericParam;
                    }
                }
            }
            if (context.DeclaringType != null) {
                return FindFNAGeneric(type, context.DeclaringType);
            }
            return type;
        }

        private static MethodReference FindFNA(this MethodReference method, MemberReference context) {
            if (!method.IsXNA()) {
                return method;
            }
            TypeReference findTypeRef = FindFNA(method.DeclaringType, context);
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
                        return foundMethod;
                    }
                }
            }

            //For anyone trying to find out why / when no method gets found: Take this!
            Console.WriteLine("debug m z a: " + method.FullName);
            Console.WriteLine("debug m z b: " + findTypeRef);
            Console.WriteLine("debug m z c: " + findType);
            Console.WriteLine("debug m z d: " + findTypeRef.Scope.Name);

            return method;
        }

        private static void patch(ModuleDefinition module, TypeDefinition type) {
            for (int i = 0; i < type.NestedTypes.Count; i++) {
                patch(module, type.NestedTypes[i]);
            }
            
            type.BaseType = module.ImportIfNeeded(type.BaseType.FindFNA());
            
            for (int ii = 0; ii < type.Fields.Count; ii++) {
                 FieldDefinition field = type.Fields[ii];
                 if (!field.FieldType.IsXNA()) {
                    continue;
                 }
                 field.FieldType = module.ImportIfNeeded(FindFNA(field.FieldType, type));
            }
            
            for (int ii = 0; ii < type.Properties.Count; ii++) {
                PropertyDefinition property = type.Properties[ii];
                if (!property.PropertyType.IsXNA()) {
                    continue;
                }
                property.PropertyType = module.ImportIfNeeded(FindFNA(property.PropertyType, type));
            }

            for (int i = 0; i < type.Methods.Count; i++) {
                MethodDefinition method = type.Methods[i];
                
                for (int ii = 0; method.HasBody && ii < method.Body.Variables.Count; ii++) {
                    //TODO debug! (Import crashes in MonoMod)
                    if (!method.Body.Variables[ii].VariableType.IsXNA()) {
                        continue;
                    }
                    method.Body.Variables[ii].VariableType = module.ImportIfNeeded(FindFNA(method.Body.Variables[ii].VariableType, method));
                }
                
                for (int ii = 0; ii < method.Parameters.Count; ii++) {
                    if (!method.Parameters[ii].ParameterType.IsXNA()) {
                        continue;
                    }
                    method.Parameters[ii].ParameterType = module.ImportIfNeeded(FindFNA(method.Parameters[ii].ParameterType, method));
                }
                
                method.ReturnType = module.ImportIfNeeded(FindFNA(method.ReturnType, method));
                
                for (int ii = 0; method.HasBody && ii < method.Body.Instructions.Count; ii++) {
                    Instruction instruction = method.Body.Instructions[ii];
                    
                    if (instruction.Operand is TypeReference) {
                        instruction.Operand = module.ImportIfNeeded(FindFNA((TypeReference) instruction.Operand, method));
                    } else if (instruction.Operand is MethodReference && ((MethodReference) instruction.Operand).IsXNA()) {
                        instruction.Operand = module.ImportIfNeeded(FindFNA((MethodReference) instruction.Operand, method));
                    } else if (instruction.Operand is MethodReference) {
                        MethodReference methodr = (MethodReference) instruction.Operand;
                        
                        for (int iii = 0; iii < methodr.Parameters.Count; iii++) {
                            if (!methodr.Parameters[iii].ParameterType.IsXNA()) {
                                continue;
                            }
                            methodr.Parameters[iii].ParameterType = module.ImportIfNeeded(FindFNA(methodr.Parameters[iii].ParameterType, method));
                        }
                        
                        instruction.Operand = methodr;
                    } else if (instruction.Operand is FieldReference) {
                        FieldReference field = (FieldReference) instruction.Operand;
    
                        TypeReference findTypeRef = FindFNA(field.DeclaringType, method);
                        TypeDefinition findType = findTypeRef == null ? null : findTypeRef.IsXNA() ? null : findTypeRef.Resolve();
                        findTypeRef = module.ImportIfNeeded(findTypeRef);
                        
                        if (findType != null) {
                            for (int iii = 0; iii < findType.Fields.Count; iii++) {
                                if (findType.Fields[iii].Name == field.Name) {
                                    FieldReference foundField = findType.Fields[iii];
                                    
                                    if (field.DeclaringType.IsGenericInstance) {
                                        foundField = module.ImportIfNeeded(new FieldReference(field.Name, FindFNA(field.FieldType, findTypeRef), findTypeRef));
                                    }
                                    
                                    field = module.ImportIfNeeded(foundField);
                                    break;
                                }
                            }
                        }
    
                        if (field == instruction.Operand) {
                            field = new FieldReference(field.Name, module.ImportIfNeeded(FindFNA(field.FieldType, method)), module.ImportIfNeeded(FindFNA(field.DeclaringType, method)));
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
                ModuleDefinition module = ModuleDefinition.ReadModule(arg);

                for (int i = 0; i < module.AssemblyReferences.Count; i++) {
                    if (module.AssemblyReferences[i].Name != "Microsoft.Xna.Framework") {
                        continue;
                    }
                    Console.WriteLine("Found reference to XNA - replacing with FNA");
                    module.AssemblyReferences[i] = new AssemblyNameReference("FNA", new Version(0, 0, 0, 1));
                }
                
                for (int i = 0; i < module.Types.Count; i++) {
                    patch(module, module.Types[i]);
                }

                module.Write(arg);
            }
            
            foreach (string arg in args) {
                Console.WriteLine("Cleaning " + arg);
                ModuleDefinition module = ModuleDefinition.ReadModule(arg);

                for (int i = 0; i < module.AssemblyReferences.Count; i++) {
                    if (module.AssemblyReferences[i].Name.StartsWith("Microsoft.Xna.Framework.")) {
                        if (module.AssemblyReferences[i].Name.EndsWith(".Xact")) {
                            continue;
                        }
                        Console.WriteLine("Found reference to XNA assembly " + module.AssemblyReferences[i].Name + " - removing");
                        module.AssemblyReferences.RemoveAt(i);
                        i--;
                        continue;
                    }
                }
                
                module.Write(arg);
            }
        }

    }
}

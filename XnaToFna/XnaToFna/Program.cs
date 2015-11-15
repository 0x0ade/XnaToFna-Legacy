using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace XnaToFna {
    static class Program {

        private static ModuleDefinition XTF;
        private static ModuleDefinition FNA;
        private static bool FixBrokenPaths = false;
        
        private static TypeDefinition xtf_Helper;
        private static Dictionary<string, MethodDefinition> xtf_Methods = new Dictionary<string, MethodDefinition>();
        
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
        
        private static string MakeMethodNameFindFriendly(string str, MethodReference method, TypeReference type, bool inner = false) {
            if (!inner) {
                int indexOfMethodDoubleColons = str.IndexOf("::");
                
                //screw generic parameters - remove them!
                int open = str.IndexOf("<", indexOfMethodDoubleColons);
                if (-1 < open) {
                    //let's just pretend generics in generics don't exist
                    int close = str.IndexOf(">", open);
                    str = str.Substring(0, open) + str.Substring(close + 1);
                }
                
                //screw multidimensional arrays - replace them!
                open = str.IndexOf("[");
                if (-1 < open && open < indexOfMethodDoubleColons) {
                    int close = str.IndexOf("]", open);
                    str = str.Substring(0, open) + "[n]" + str.Substring(close + 1);
                }
                
                open = str.IndexOf("(", indexOfMethodDoubleColons);
                if (-1 < open) {
                    //Methods without () would be weird...
                    //Well, make the params find-friendly
                    int close = str.IndexOf(")", open);
                    str = str.Substring(0, open) + MakeMethodNameFindFriendly(str.Substring(open, close - open + 1), method, type, true) + str.Substring(close + 1);
                }
                
                return str;
            }
            
            for (int i = 0; i < type.GenericParameters.Count; i++) {
                str = str.Replace("("+type.GenericParameters[i].Name+",", "(!"+i+",");
                str = str.Replace(","+type.GenericParameters[i].Name+",", ",!"+i+",");
                str = str.Replace(","+type.GenericParameters[i].Name+")", ",!"+i+")");
                str = str.Replace("("+type.GenericParameters[i].Name+")", "(!"+i+")");
                int param = str.IndexOf(type.GenericParameters[i].Name+"[");
                if (-1 < param) {
                    str = str.Substring(0, param) + "!"+i + str.Substring(param + type.GenericParameters[i].Name.Length);
                }
            }
            for (int i = 0; i < method.GenericParameters.Count; i++) {
                str = str.Replace("("+method.GenericParameters[i].Name+",", "(!!"+i+",");
                str = str.Replace(","+method.GenericParameters[i].Name+",", ",!!"+i+",");
                str = str.Replace(","+method.GenericParameters[i].Name+")", ",!!"+i+")");
                str = str.Replace("("+method.GenericParameters[i].Name+")", "(!!"+i+")");
                int param = str.IndexOf(method.GenericParameters[i].Name+"[");
                if (-1 < param) {
                    str = str.Substring(0, param) + "!!"+i + str.Substring(param + method.GenericParameters[i].Name.Length);
                }
            }
            return str;
        }

        private static TypeReference FindFNA(this TypeReference type, MemberReference context = null, bool fallbackOnXNA = true) {
            if (type == null) {
                Console.WriteLine("Can't find null type! Context: " + (context == null ? "none" : context.FullName));
                Console.WriteLine(Environment.StackTrace);
                return null;
            }
            TypeReference foundType = type.IsXNA() ? FNA.GetType(type.FullName) : null;
            if (foundType == null && type.IsByReference) {
                foundType = new ByReferenceType(type.GetElementType().FindFNA(context));
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
            return (type.IsXNA() ? Module.ImportIfNeeded(foundType) : null) ?? (fallbackOnXNA || !type.Scope.Name.StartsWith("Microsoft.Xna.Framework") ? type : null);
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
            TypeReference findTypeRef = method.DeclaringType.FindFNA(context, false);
            TypeDefinition findType = findTypeRef == null ? null : findTypeRef.IsDefinition ? (TypeDefinition) findTypeRef : findTypeRef.Resolve();
            
            if (findType != null && !method.DeclaringType.IsArray) {
                string methodName = method.FullName;
                methodName = methodName.Substring(methodName.IndexOf(" ") + 1);
                methodName = MakeMethodNameFindFriendly(methodName, method, findType);
                for (int ii = 0; ii < findType.Methods.Count; ii++) {
                    MethodReference foundMethod = findType.Methods[ii];
                    string foundMethodName = foundMethod.FullName;
                    foundMethodName = foundMethodName.Replace(findType.FullName, findTypeRef.FullName);
                    foundMethodName = foundMethodName.Substring(foundMethodName.IndexOf(" ") + 1);
                    //TODO find a better way to compare methods / fix comparing return types
                    foundMethodName = MakeMethodNameFindFriendly(foundMethodName, foundMethod, findType);

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

            if (!method.DeclaringType.IsArray) {
                Console.WriteLine("Method not found     : " + method.FullName);
                Console.WriteLine("Found type reference : " + findTypeRef);
                Console.WriteLine("Found type definition: " + findType);
                if (findTypeRef != null) {
                    Console.WriteLine("Found type scope     : " + findTypeRef.Scope.Name);
                }
                
                if (findType != null) {
                    string methodName = method.FullName;
                    methodName = methodName.Substring(methodName.IndexOf(" ") + 1);
                    methodName = MakeMethodNameFindFriendly(methodName, method, findType);
                    Console.WriteLine("debug m -1 / " + (findType.Methods.Count - 1) + ": " + methodName);
                    for (int ii = 0; ii < findType.Methods.Count; ii++) {
                        MethodReference foundMethod = findType.Methods[ii];
                        string foundMethodName = foundMethod.FullName;
                        foundMethodName = foundMethodName.Replace(findType.FullName, findTypeRef.FullName);
                        foundMethodName = foundMethodName.Substring(foundMethodName.IndexOf(" ") + 1);
                        //TODO find a better way to compare methods / fix comparing return types
                        foundMethodName = MakeMethodNameFindFriendly(foundMethodName, foundMethod, findType);
                        Console.WriteLine("debug m "+ii+" / " + (findType.Methods.Count - 1) + ": " + foundMethodName);
                    }
                }
            }
            
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
                
                for (int ii = 0; method.HasBody && ii < method.Body.Variables.Count; ii++) {
                    method.Body.Variables[ii].VariableType = method.Body.Variables[ii].VariableType.FindFNA(method);
                }
                
                for (int ii = 0; ii < method.Parameters.Count; ii++) {
                    method.Parameters[ii].ParameterType = method.Parameters[ii].ParameterType.FindFNA(method);
                }
                
                method.ReturnType = method.ReturnType.FindFNA(method);
                
                for (int ii = 0; method.HasBody && ii < method.Body.Instructions.Count; ii++) {
                    Instruction instruction = method.Body.Instructions[ii];
                    
                    if (instruction.OpCode == OpCodes.Ldstr) {
                        string str = (string) instruction.Operand;
                        bool pathFixed = false;
                        
                        if (!str.StartsWith("Content\\") && str.Contains("\\") && Path.DirectorySeparatorChar != '\\' && (instruction.Next.OpCode != OpCodes.Call || ((MethodReference) instruction.Next.Operand).DeclaringType.Scope.Name != "XnaToFna")) {
                            Console.WriteLine("Broken path (\\ vs /) in " + method.DeclaringType.FullName + "." + method.Name + " (IL_" + (instruction.Offset.ToString("x4")) + "): " + str);
                            if (FixBrokenPaths) {
                                ILProcessor il = method.Body.GetILProcessor();
                                
                                //FIXME this makes the assembly read-only via Mono.Cecil...
                                il.InsertAfter(instruction, il.Create(OpCodes.Call, Module.Import(xtf_Methods["PatchPath"])));
                                
                                pathFixed = true;
                            }
                        }
                        
                        if (str.Replace('\\', Path.DirectorySeparatorChar).Contains(Path.DirectorySeparatorChar.ToString()) && !File.Exists("." + str) && !Directory.Exists("." + str) && !File.Exists(Path.Combine(".", "Content", str)) && !Directory.Exists(Path.Combine(".", "Content", str))) {
                            str = str.Replace('\\', Path.DirectorySeparatorChar);
                            bool tmpContent = !str.StartsWith(Path.DirectorySeparatorChar + "Content");
                            int startIndex = 0;
                            if (tmpContent) {
                                string tmpStr = str;
                                str = Path.DirectorySeparatorChar + Path.Combine("Content", str);
                                startIndex = str.IndexOf(tmpStr);
                            }
                            string found = "."+Path.DirectorySeparatorChar;
                            string[] levels = Path.Combine(".", str.ToLowerInvariant()).Split(Path.DirectorySeparatorChar);
                            int level = 1;
                            for (; level < levels.Length; level++) {
                                bool subFound = false;
                                
                                foreach (string sub_ in Directory.EnumerateDirectories(found)) {
                                    string sub = sub_.Substring(found.Length);
                                    if (sub.IndexOf(Path.DirectorySeparatorChar) == 0) {
                                        sub = sub.Substring(1);
                                    }
                                    if (sub.ToLower() == levels[level]) {
                                        found = Path.Combine(found, sub);
                                        subFound = true;
                                        break;
                                    }
                                }
                                
                                if (subFound) {
                                    continue;
                                } else if (level < levels.Length - 1) {
                                    Console.WriteLine("A directory on the path cannot be found case-insensitively!");
                                    Console.WriteLine("Path: " + str);
                                    Console.WriteLine("Found: " + found);
                                    break;
                                }
                                
                                if (level < levels.Length - 1) {
                                    continue;
                                }
                                
                                foreach (string sub_ in Directory.EnumerateFiles(found)) {
                                    string sub = sub_.Substring(found.Length);
                                    if (sub.IndexOf(Path.DirectorySeparatorChar) == 0) {
                                        sub = sub.Substring(1);
                                    }
                                    if (sub.ToLower() == levels[level]) {
                                        found = Path.Combine(found, sub);
                                        subFound = true;
                                        break;
                                    }
                                }
                                
                                if (!subFound) {
                                    Console.WriteLine("The file cannot be found case-insensitively!");
                                    Console.WriteLine("Path: " + str);
                                    Console.WriteLine("Found: " + found);
                                    break;
                                }
                            }
                            if (level == levels.Length) {
                                str = found;
                                pathFixed = true;
                            }
                            str = str.Substring(startIndex);
                        }
                        
                        if (pathFixed) {
                            Console.WriteLine("New path: " + str);
                        }
                        instruction.Operand = str;
                        continue;
                    }
                    
                    if (instruction.OpCode == OpCodes.Ldstr && ((string) instruction.Operand).Contains("\\")) {
                        if (FixBrokenPaths) {
                            instruction.Operand = ((string) instruction.Operand).Replace("\\", "/");
                        } else {
                            Console.WriteLine("Broken path in " + method.DeclaringType.FullName + "." + method.Name + " (IL_" + (instruction.Offset.ToString("x4")) + "): " + ((string) instruction.Operand));
                        }
                    }
                    
                    if (instruction.Operand is TypeReference) {
                        instruction.Operand = ((TypeReference) instruction.Operand).FindFNA(method);
                    } else if (instruction.Operand is MethodReference && ((MethodReference) instruction.Operand).IsXNA()) {
                        instruction.Operand = ((MethodReference) instruction.Operand).FindFNA(method);
                    } else if (instruction.Operand is FieldReference && ((FieldReference) instruction.Operand).IsXNA()) {
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
            Console.WriteLine("Loading XnaToFna");
            XTF = ModuleDefinition.ReadModule(System.Reflection.Assembly.GetExecutingAssembly().Location);
            
            Console.WriteLine("Preparing references to XnaToFnaHelper");
            xtf_Helper = XTF.GetType("XnaToFna.XnaToFnaHelper");
            foreach (MethodDefinition method in xtf_Helper.Methods) {
                xtf_Methods[method.Name] = method;
            }
            
            Console.WriteLine("Loading FNA");
            FNA = ModuleDefinition.ReadModule("FNA.dll");

            foreach (string arg in args) {
                if (arg == "--paths") {
                    FixBrokenPaths = !FixBrokenPaths;
                    Console.WriteLine("Fixing broken paths has been " + (FixBrokenPaths ? "en" : "dis") + "abled.");
                    continue;
                }
                
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

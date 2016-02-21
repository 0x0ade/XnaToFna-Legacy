using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace XnaToFna {
    static class Program {

        private static ModuleDefinition XTF;
        private static ModuleDefinition FNA;
        private static ModuleDefinition MGNET;
        private static bool FixBrokenPaths = false;
        
        private static TypeDefinition xtf_Helper;
        private static Dictionary<string, MethodDefinition> xtf_Methods = new Dictionary<string, MethodDefinition>();
        
        private static ModuleDefinition Module;
        
        private static string[] XNAScope = {"Microsoft.Xna.Framework", "FNA"};
        private static string[] NETNSScope = {"Microsoft.Xna.Framework.Net", "MonoGame.Framework.Net"}; //TODO the latter is just guessed

        
        private static TypeReference ImportIfNeeded(this ModuleDefinition module, TypeReference r) {
            return r == null ? null : r.Module.Name != module.Name ? module.Import(r) : r;
        }
        private static MethodReference ImportIfNeeded(this ModuleDefinition module, MethodReference r) {
            return r == null ? null : r.Module.Name != module.Name ? module.Import(r) : r;
        }
        private static FieldReference ImportIfNeeded(this ModuleDefinition module, FieldReference r) {
            return r == null ? null : r.Module.Name != module.Name ? module.Import(r) : r;
        }
        
        private static bool IsIn(this TypeReference r, params string[] scope) {
            if (r == null) {
                return false;
            }
            if (r.GetElementType() != null && r.GetElementType() != r && r.GetElementType().IsIn(scope)) {
                return true;
            }
            if (r.IsGenericInstance) {
                foreach (TypeReference genericArgument in ((GenericInstanceType) r).GenericArguments) {
                    if (genericArgument.IsIn(scope)) {
                        return true;
                    }
                }
            }
            for (int i = 0; i < scope.Length; i++) {
                if (r.Scope.Name.Contains(scope[i])) {
                    return true;
                }
            }
            return false;
        }
        private static bool IsIn(this MethodReference r, params string[] scope) {
            if (r == null) {
                return false;
            }
            if (r.IsGenericInstance) {
                foreach (TypeReference genericArgument in ((GenericInstanceMethod) r).GenericArguments) {
                    if (genericArgument.IsIn(scope)) {
                        return true;
                    }
                }
            }
            for (int i = 0; i < r.Parameters.Count; i++) {
                if (r.Parameters[i].ParameterType.IsIn(scope)) {
                    return true;
                }
            }
            return r.DeclaringType.IsIn(scope) || r.ReturnType.IsIn(scope);
        }
        private static bool IsIn(this FieldReference r, params string[] scope) {
            if (r == null) {
                return false;
            }
            return r.DeclaringType.IsIn(scope) || r.FieldType.IsIn(scope);
        }
        
        private static string MakeMethodNameFindFriendly(string str, MethodReference method, TypeReference type, bool inner = false, string[] genParams = null) {
            while (method.IsGenericInstance) {
                method = ((GenericInstanceMethod) method).ElementMethod;
            }

            if (!inner) {
                int indexOfMethodDoubleColons = str.IndexOf("::");
                int openArgs = str.IndexOf("(", indexOfMethodDoubleColons);
                int numGenParams = 0;

                //screw generic parameters - replace them!
                int open = indexOfMethodDoubleColons;
                if (-1 < (open = str.IndexOf("<", open + 1)) && open < openArgs) {
                    int close_ = open;
                    int close = close_;
                    while (-1 < (close_ = str.IndexOf(">", close_ + 1)) && close_ < openArgs) {
                        close = close_;
                    }

                    numGenParams = method.GenericParameters.Count;
                    if (numGenParams == 0) {
                        numGenParams = 1;
                        //GenericParams.Count is 0 (WHY?) so we must count ,s
                        int level = 0;
                        for (int i = open; i < close; i++) {
                            if (str[i] == '<') {
                                level++;
                            } else if (str[i] == '>') {
                                level--;
                            } else if (str[i] == ',' && level == 0) {
                                numGenParams++;
                            }
                        }
                        genParams = new string[numGenParams];
                        int j = 0;
                        //Simply approximate that the generic parameters MUST exist in the parameters in correct order...
                        for (int i = 0; i < method.Parameters.Count && j < genParams.Length; i++) {
                            TypeReference paramType = method.Parameters[i].ParameterType;
                            while (paramType.IsArray || paramType.IsByReference) {
                                paramType = paramType.GetElementType();
                            }
                            if (paramType.IsGenericParameter) {
                                genParams[j] = paramType.Name;
                                j++;
                            }
                        }
                    }

                    str = str.Substring(0, open + 1) + numGenParams + str.Substring(close);
                    openArgs = str.IndexOf("(", indexOfMethodDoubleColons);
                }

                //add them if missing
                open = str.IndexOf("<", indexOfMethodDoubleColons);
                if ((open <= -1 || openArgs < open) && method.HasGenericParameters) {
                    int pos = indexOfMethodDoubleColons + 2 + method.Name.Length;
                    str = str.Substring(0, pos) + "<" + method.GenericParameters.Count + ">" + str.Substring(pos);
                    openArgs = str.IndexOf("(", indexOfMethodDoubleColons);
                }

                //screw multidimensional arrays - replace them!
                open = str.IndexOf("[");
                if (-1 < open && open < indexOfMethodDoubleColons) {
                    int close = str.IndexOf("]", open);
                    int n = 1;
                    int i = open;
                    while (-1 < (i = str.IndexOf(",", i + 1)) && i < close) {
                        n++;
                    }
                    str = str.Substring(0, open + 1) + n + str.Substring(close);
                    openArgs = str.IndexOf("(", indexOfMethodDoubleColons);
                }

                if (method.GenericParameters.Count != 0) {
                    numGenParams = method.GenericParameters.Count;
                    genParams = new string[numGenParams];
                    for (int i = 0; i < method.GenericParameters.Count; i++) {
                        genParams[i] = method.GenericParameters[i].Name;
                    }
                }

                //screw arg~ oh, wait, that's what we're trying to fix. Continue on.
                open = openArgs;
                if (-1 < open) {
                    //Methods without () would be weird...
                    //Well, make the params find-friendly
                    int close = str.IndexOf(")", open);
                    str = str.Substring(0, open) + MakeMethodNameFindFriendly(str.Substring(open, close - open + 1), method, type, true, genParams) + str.Substring(close + 1);
                    openArgs = str.IndexOf("(", indexOfMethodDoubleColons);
                }

                return str;
            }

            for (int i = 0; i < type.GenericParameters.Count; i++) {
                str = str.Replace("("+type.GenericParameters[i].Name+",", "(!"+i+",");
                str = str.Replace(","+type.GenericParameters[i].Name+",", ",!"+i+",");
                str = str.Replace(","+type.GenericParameters[i].Name+")", ",!"+i+")");
                str = str.Replace("("+type.GenericParameters[i].Name+")", "(!"+i+")");

                str = str.Replace("("+type.GenericParameters[i].Name+"&", "(!"+i+"&");
                str = str.Replace(","+type.GenericParameters[i].Name+"&", ",!"+i+"&");
                str = str.Replace(","+type.GenericParameters[i].Name+"&", ",!"+i+"&");
                str = str.Replace("("+type.GenericParameters[i].Name+"&", "(!"+i+"&");

                str = str.Replace("("+type.GenericParameters[i].Name+"[", "(!"+i+"[");
                str = str.Replace(","+type.GenericParameters[i].Name+"[", ",!"+i+"[");
                str = str.Replace(","+type.GenericParameters[i].Name+"[", ",!"+i+"[");
                str = str.Replace("("+type.GenericParameters[i].Name+"[", "(!"+i+"[");

                str = str.Replace("<"+type.GenericParameters[i].Name+",", "<!"+i+",");
                str = str.Replace(","+type.GenericParameters[i].Name+">", ",!"+i+">");
                str = str.Replace("<"+type.GenericParameters[i].Name+">", "<!"+i+">");
            }
            if (genParams == null) {
                return str;
            }

            for (int i = 0; i < genParams.Length; i++) {
                str = str.Replace("("+genParams[i]+",", "(!!"+i+",");
                str = str.Replace(","+genParams[i]+",", ",!!"+i+",");
                str = str.Replace(","+genParams[i]+")", ",!!"+i+")");
                str = str.Replace("("+genParams[i]+")", "(!!"+i+")");

                str = str.Replace("("+genParams[i]+"&", "(!!"+i+"&");
                str = str.Replace(","+genParams[i]+"&", ",!!"+i+"&");
                str = str.Replace(","+genParams[i]+"&", ",!!"+i+"&");
                str = str.Replace("("+genParams[i]+"&", "(!!"+i+"&");

                str = str.Replace("("+genParams[i]+"[", "(!!"+i+"[");
                str = str.Replace(","+genParams[i]+"[", ",!!"+i+"[");
                str = str.Replace(","+genParams[i]+"[", ",!!"+i+"[");
                str = str.Replace("("+genParams[i]+"[", "(!!"+i+"[");

                str = str.Replace("<"+genParams[i]+",", "<!!"+i+",");
                str = str.Replace(","+genParams[i]+">", ",!!"+i+">");
                str = str.Replace("<"+genParams[i]+">", "<!!"+i+">");
            }
            return str;
        }

        private static TypeReference FindFNA(this TypeReference type, MemberReference context = null, bool fallbackOnXNA = true) {
            if (type == null) {
                Console.WriteLine("Can't find null type! Context: " + (context == null ? "none" : context.FullName));
                Console.WriteLine(Environment.StackTrace);
                return null;
            }
            TypeReference foundType = null;
            if (type.IsIn(NETNSScope)) {
                foundType = MGNET.GetType(type.FullName);
            } else if (type.IsIn(XNAScope)) {
                foundType = FNA.GetType(type.FullName);
            }
            if (foundType == null && type.IsByReference) {
                foundType = new ByReferenceType(((ByReferenceType) type).ElementType.FindFNA(context));
            }
            if (foundType == null && type.IsArray) {
                foundType = new ArrayType(Module.ImportIfNeeded(((ArrayType) type).ElementType.FindFNA(context)));
            }
            if (foundType == null && context != null && type.IsGenericParameter) {
                foundType = type.FindFNAGeneric(context); 
            }
            if (foundType == null && context != null && type.IsGenericInstance) {
                foundType = new GenericInstanceType(Module.ImportIfNeeded(((GenericInstanceType) type).ElementType.FindFNA(context)));
                foreach (TypeReference genericArgument in ((GenericInstanceType) type).GenericArguments) {
                    ((GenericInstanceType) foundType).GenericArguments.Add(Module.ImportIfNeeded(genericArgument.FindFNA(context)));
                }
            }
            if (type.IsIn(XNAScope) && foundType == null) {
                Console.WriteLine("debug: Could not find type " + type.FullName + " [" + type.Scope.Name + "] in FNA!");
            }
            return (type.IsIn(XNAScope) ? Module.ImportIfNeeded(foundType) : null) ?? (fallbackOnXNA || !type.Scope.Name.StartsWith("Microsoft.Xna.Framework") ? type : null);
        }

        private static TypeReference FindFNAGeneric(this TypeReference type, MemberReference context) {
            if (context is MethodReference) {
                MethodReference r = ((MethodReference) context).GetElementMethod();
                for (int gi = 0; gi < r.GenericParameters.Count; gi++) {
                    GenericParameter genericParam = r.GenericParameters[gi];
                    if (genericParam.FullName == type.FullName) {
                        //TODO variables hate me, import otherwise
                        return genericParam;
                    }
                }
                if (type.Name.StartsWith("!!")) {
                    int i;
                    if (int.TryParse(type.Name.Substring(2), out i)) {
                        return r.GenericParameters[i];
                    }
                }
            }
            if (context is TypeReference) {
                TypeReference r = ((TypeReference) context).GetElementType();
                for (int gi = 0; gi < r.GenericParameters.Count; gi++) {
                    GenericParameter genericParam = r.GenericParameters[gi];
                    if (genericParam.FullName == type.FullName) {
                        //TODO variables hate me, import otherwise
                        return genericParam;
                    }
                }
                if (type.Name.StartsWith("!!")) {
                    return type.FindFNAGeneric(context.DeclaringType);
                } else if (type.Name.StartsWith("!")) {
                    int i;
                    if (int.TryParse(type.Name.Substring(1), out i)) {
                        return r.GenericParameters[i];
                    }
                }
            }
            if (context != null && context.DeclaringType != null) {
                return type.FindFNAGeneric(context.DeclaringType);
            }
            return type;
        }

        private static MethodReference FindFNA(this MethodReference method, MemberReference context) {
            if (!method.IsIn(XNAScope)) {
                return method;
            }
            TypeReference findTypeRef = method.DeclaringType.FindFNA(context, false);
            TypeDefinition findType = findTypeRef == null || findTypeRef.IsArray ? null : findTypeRef.Resolve();
            
            if (findType != null && !method.DeclaringType.IsArray) {
                bool typeMismatch = findType.FullName != method.DeclaringType.FullName;

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
                        if (typeMismatch && method.DeclaringType.IsGenericInstance) {
                            //TODO test return type context
                            MethodReference genMethod = new MethodReference(method.Name, FindFNA(method.ReturnType, findTypeRef), findTypeRef);
                            genMethod.CallingConvention = method.CallingConvention;
                            genMethod.HasThis = method.HasThis;
                            genMethod.ExplicitThis = method.ExplicitThis;
                            for (int i = 0; i < method.GenericParameters.Count; i++) {
                                genMethod.GenericParameters.Add((GenericParameter) (FindFNA(method.GenericParameters[i], genMethod, false) ?? FindFNA(method.GenericParameters[i], findTypeRef)));
                            }
                            for (int i = 0; i < method.Parameters.Count; i++) {
                                genMethod.Parameters.Add(new ParameterDefinition(FindFNA(method.Parameters[i].ParameterType, genMethod)));
                            }

                            foundMethod = Module.Import(genMethod);
                        }

                        if (foundMethod.Module != Module) {
                            foundMethod = Module.Import(foundMethod);
                        }

                        if (method.IsGenericInstance) {
                            GenericInstanceMethod genMethod = new GenericInstanceMethod(foundMethod);
                            GenericInstanceMethod methodg = ((GenericInstanceMethod) method);

                            for (int i = 0; i < methodg.GenericArguments.Count; i++) {
                                genMethod.GenericArguments.Add(FindFNA(methodg.GenericArguments[i], context, false) ?? FindFNA(methodg.GenericArguments[i], genMethod, true));
                            }

                            foundMethod = genMethod;
                        }

                        return foundMethod;
                    }
                }
            }

            if (!method.DeclaringType.IsArray) {
                Console.WriteLine("debug: Method not found     : " + method.FullName);
                Console.WriteLine("debug: Method type scope    : " + method.DeclaringType.Scope.Name);
                Console.WriteLine("debug: Found type reference : " + findTypeRef);
                Console.WriteLine("debug: Found type definition: " + findType);
                if (findTypeRef != null) {
                    Console.WriteLine("debug: Found type scope     : " + findTypeRef.Scope.Name);
                }

                if (findType != null) {
                    string methodName = method.FullName;
                    methodName = methodName.Substring(methodName.IndexOf(" ") + 1);
                    methodName = MakeMethodNameFindFriendly(methodName, method, findType);
                    Console.WriteLine("debug: m -1 / " + (findType.Methods.Count - 1) + ": " + methodName);
                    for (int ii = 0; ii < findType.Methods.Count; ii++) {
                        MethodReference foundMethod = findType.Methods[ii];
                        string foundMethodName = foundMethod.FullName;
                        foundMethodName = foundMethodName.Replace(findType.FullName, findTypeRef.FullName);
                        foundMethodName = foundMethodName.Substring(foundMethodName.IndexOf(" ") + 1);
                        //TODO find a better way to compare methods / fix comparing return types
                        foundMethodName = MakeMethodNameFindFriendly(foundMethodName, foundMethod, findType);
                        Console.WriteLine("debug: m "+ii+" / " + (findType.Methods.Count - 1) + ": " + foundMethodName);
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
                 if (!field.FieldType.IsIn(XNAScope)) {
                    continue;
                 }
                 field.FieldType = field.FieldType.FindFNA(type);
            }
            
            for (int ii = 0; ii < type.Properties.Count; ii++) {
                PropertyDefinition property = type.Properties[ii];
                if (!property.PropertyType.IsIn(XNAScope)) {
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
                        //possibly fix paths
                        string str = (string) instruction.Operand;
                        bool pathFixed = false;
                        
                        //\ vs /
                        if (
                            (!str.StartsWith("Content\\") || str == "Content\\") && str.Contains("\\") && Path.DirectorySeparatorChar != '\\' &&
                            (instruction.Next.OpCode != OpCodes.Call || ((MethodReference) instruction.Next.Operand).Name != "PatchPath")
                        ) {
                            //this is quite "harmless" and spammed STDOUT.
                            if (FixBrokenPaths) {
                                Console.WriteLine("Broken path (\\ vs /) in " + method.DeclaringType.FullName + "." + method.Name + " (IL_" + (instruction.Offset.ToString("x4")) + "): " + str);
                                ILProcessor il = method.Body.GetILProcessor();
                                
                                //FIXME this makes the assembly read-only via Mono.Cecil...
                                il.InsertAfter(instruction, il.Create(OpCodes.Call, Module.Import(xtf_Methods["PatchPath"])));
                            }
                        }
                        
                        //case mismatch
                        //FIXME this is definitely optimizable!
                        if (
                            str.Replace('\\', Path.DirectorySeparatorChar).Contains(Path.DirectorySeparatorChar.ToString()) &&
                            !File.Exists("." + str) && !Directory.Exists("." + str) &&
                            !File.Exists(Path.Combine(".", "Content", str)) && !Directory.Exists(Path.Combine(".", "Content", str))
                        ) {
                            string strOrig = str;
                            str = str.Replace('\\', Path.DirectorySeparatorChar);
                            bool tmpContent = str.IndexOf("Content") < 0 || 1 < str.IndexOf("Content");
                            int startIndex = 1; //remove the dot
                            if (str.IndexOf(Path.DirectorySeparatorChar) != 0) {
                                startIndex = 2; //remove the dash
                            }
                            if (tmpContent) {
                                string tmpStr = str;
                                str = Path.DirectorySeparatorChar + Path.Combine("Content", str);
                                startIndex += str.IndexOf(tmpStr) - 1; //remove the temporary content
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
                                    if (Path.Combine(found.Substring(1), sub).ToLowerInvariant() + Path.DirectorySeparatorChar == str.ToLowerInvariant()) {
                                        found = Path.Combine(found, sub) + Path.DirectorySeparatorChar;
                                        level = levels.Length - 1;
                                        subFound = true;
                                        break;
                                    }
                                    if (sub.ToLowerInvariant() == levels[level]) {
                                        found = Path.Combine(found, sub);
                                        subFound = true;
                                        break;
                                    }
                                }
                                
                                if (subFound) {
                                    continue;
                                } else if (level < levels.Length - 1) {
                                    if (1 < level) {
                                        Console.WriteLine("A directory on the path cannot be found case-insensitively!");
                                        Console.WriteLine("String: " + str);
                                        Console.WriteLine("String (orig): " + strOrig);
                                        Console.WriteLine("Path: " + str);
                                        Console.WriteLine("Found: " + found);
                                    }
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
                                    if (sub.ToLowerInvariant() == levels[level]) {
                                        found = Path.Combine(found, sub);
                                        subFound = true;
                                    }
                                    if (sub.ToLowerInvariant() == levels[level] + ".xnb") {
                                        found = Path.Combine(found, sub.Substring(0, sub.Length - 4));
                                        subFound = true;
                                    }
                                    if (subFound) {
                                        break;
                                    }
                                }
                                
                                if (!subFound) {
                                    if (1 < level) {
                                        Console.WriteLine("A directory on the path cannot be found case-insensitively!");
                                        Console.WriteLine("String: " + str);
                                        Console.WriteLine("String (orig): " + strOrig);
                                        Console.WriteLine("Path: " + str);
                                        Console.WriteLine("Found: " + found);
                                    }
                                    break;
                                }
                            }
                            if (level >= levels.Length && (found = found.Substring(startIndex)) != strOrig) {
                                if (found != str.Substring(startIndex - 1)) {
                                    Console.WriteLine("Broken path in " + method.DeclaringType.FullName + "." + method.Name + " (IL_" + (instruction.Offset.ToString("x4")) + "): " + strOrig);
                                    if (FixBrokenPaths) {
                                        str = found;
                                        pathFixed = true;
                                    } else {
                                        str = strOrig;
                                    }
                                } else {
                                    str = strOrig;
                                }
                            } else {
                                str = strOrig;
                            }
                        }
                        
                        if (pathFixed) {
                            Console.WriteLine("New path: " + str);
                        }
                        instruction.Operand = str;
                        continue;
                    }
                    
                    //Does this even fix anything? The above path fixes should fix this.
                    /*
                    if (instruction.OpCode == OpCodes.Ldstr && ((string) instruction.Operand).Contains("\\")) {
                        if (FixBrokenPaths) {
                            instruction.Operand = ((string) instruction.Operand).Replace("\\", "/");
                        } else {
                            Console.WriteLine("Broken path in " + method.DeclaringType.FullName + "." + method.Name + " (IL_" + (instruction.Offset.ToString("x4")) + "): " + ((string) instruction.Operand));
                        }
                    }
                    */
                    
                    if (instruction.Operand is TypeReference) {
                        instruction.Operand = ((TypeReference) instruction.Operand).FindFNA(method);
                    } else if (instruction.Operand is MethodReference && ((MethodReference) instruction.Operand).IsIn(XNAScope)) {
                        instruction.Operand = ((MethodReference) instruction.Operand).FindFNA(method);
                    } else if (instruction.Operand is FieldReference && ((FieldReference) instruction.Operand).IsIn(XNAScope)) {
                        FieldReference field = (FieldReference) instruction.Operand;
    
                        TypeReference findTypeRef = field.DeclaringType.FindFNA(method);
                        TypeDefinition findType = findTypeRef == null ? null : findTypeRef.IsIn(XNAScope) ? null : findTypeRef.Resolve();
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
            XTF = ModuleDefinition.ReadModule(System.Reflection.Assembly.GetExecutingAssembly().Location, new ReaderParameters(ReadingMode.Immediate));
            
            Console.WriteLine("Preparing references to XnaToFnaHelper");
            xtf_Helper = XTF.GetType("XnaToFna.XnaToFnaHelper");
            foreach (MethodDefinition method in xtf_Helper.Methods) {
                xtf_Methods[method.Name] = method;
            }
            
            Console.WriteLine("Loading FNA");
            FNA = ModuleDefinition.ReadModule("FNA.dll", new ReaderParameters(ReadingMode.Immediate));
            
            if (File.Exists("MonoGame.Framework.Net.dll")) {
                Console.WriteLine("Found MonoGame.Framework.Net.dll - Loading MGNET");
                MGNET = ModuleDefinition.ReadModule("MonoGame.Framework.Net.dll", new ReaderParameters(ReadingMode.Immediate));
            } else {
                NETNSScope = new string[0];
            }

            foreach (string arg in args) {
                if (arg == "--paths") {
                    FixBrokenPaths = !FixBrokenPaths;
                    Console.WriteLine("Fixing broken paths has been " + (FixBrokenPaths ? "en" : "dis") + "abled.");
                    continue;
                }
                
                Console.WriteLine("Patching " + arg);
                Module = ModuleDefinition.ReadModule(arg, new ReaderParameters(ReadingMode.Immediate));

                for (int i = 0; i < Module.AssemblyReferences.Count; i++) {
                    if (Module.AssemblyReferences[i].Name == "Microsoft.Xna.Framework") {
                        Console.WriteLine("Found reference to XNA - replacing with FNA");
                        Module.AssemblyReferences[i] = new AssemblyNameReference("FNA", new Version(0, 0, 0, 1));
                        continue;
                    }
                    if (MGNET != null && Module.AssemblyReferences[i].Name == "Microsoft.Xna.Framework.Net") {
                        Console.WriteLine("Found reference to the XNA NET namespace - replacing with MGNET");
                        Module.AssemblyReferences[i] = new AssemblyNameReference("MonoGame.Framework.Net", new Version(0, 0, 0, 1));
                        continue;
                    }
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
                }
                
                for (int i = 0; i < Module.Types.Count; i++) {
                    patch(Module.Types[i]);
                }

                Module.Write(arg);
                //For those brave enough: Feel free to fix the error caused by uncommenting the following lines.
                //FIXME find the cause of the unreadwritability bug
                //Module = ModuleDefinition.ReadModule(arg, new ReaderParameters(ReadingMode.Immediate));
                //Module.Write(arg);
            }
        }

    }
}

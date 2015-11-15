using System;
using System.IO;

namespace XnaToFna {
    public static class XnaToFnaHelper {
        
        public static string PatchPath(this string str) {
            return str.Replace('\\', Path.DirectorySeparatorChar);
        }
        
    }
}

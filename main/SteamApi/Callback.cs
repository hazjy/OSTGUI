#nullable disable

/* Copyright (c) 2024 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace SAM.API
{
    public abstract class Callback : ICallback
    {
        public delegate void CallbackFunction(IntPtr param);

        public event CallbackFunction OnRun;

        public abstract int Id { get; }
        public abstract bool IsServer { get; }

        public void Run(IntPtr param)
        {
            this.OnRun(param);
        }
    }

    // AOT 适配：Marshal.PtrToStructure<T> 要求 T 声明构造器可见性（否则 ILC 报 IL2091）——
    // 实例化 T 由 marshaller 内部完成（含委托类型），这里按 ILC 的要求原样声明。
    public abstract class Callback<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
        TParameter> : ICallback
        where TParameter : struct
    {
        public delegate void CallbackFunction(TParameter arg);

        public event CallbackFunction OnRun;

        public abstract int Id { get; }
        public abstract bool IsServer { get; }

        public void Run(IntPtr pvParam)
        {
            var data = Marshal.PtrToStructure<TParameter>(pvParam);
            this.OnRun(data);
        }
    }
}

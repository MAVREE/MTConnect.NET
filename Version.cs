﻿// Copyright (c) 2020 TrakHound Inc., All Rights Reserved.

// This file is subject to the terms and conditions defined in
// file 'LICENSE', which is part of this source code package.

namespace MTConnect
{
    public class Version
    {
        public static double Get(string xml)
        {
            var ns = Namespaces.Get(xml);
            if (ns != null)
            {
                if (Namespaces.v17.Match(ns)) return 1.7;
                if (Namespaces.v16.Match(ns)) return 1.6;
                if (Namespaces.v15.Match(ns)) return 1.5;
                if (Namespaces.v14.Match(ns)) return 1.4;
                if (Namespaces.v13.Match(ns)) return 1.3;
                if (Namespaces.v12.Match(ns)) return 1.2;
                if (Namespaces.v11.Match(ns)) return 1.1;
            }

            return -1;
        }
    }
}

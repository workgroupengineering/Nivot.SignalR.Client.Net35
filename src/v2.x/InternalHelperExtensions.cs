﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

namespace Microsoft.AspNet.SignalR.Client
{
    internal static class InternalHelperExtensions
    {

        internal static CookieCollection GetAllCookies(this CookieContainer cookieJar) {

            try {
                var obj = (CookieCollection) cookieJar.GetType().InvokeMember("cookies", BindingFlags.NonPublic |
                                                                                         BindingFlags.GetField |
                                                                                         BindingFlags.Instance,
                    null,
                    cookieJar,
                    new object[] {});
                return obj;
            } catch {
                CookieCollection cookieCollection = new CookieCollection();


                Hashtable table = (Hashtable)cookieJar.GetType().InvokeMember("m_domainTable",
                                                                                BindingFlags.NonPublic |
                                                                                BindingFlags.GetField |
                                                                                BindingFlags.Instance,
                                                                                null,
                                                                                cookieJar,
                                                                                new object[] { });

                foreach (var tableKey in table.Keys) {
                    String str_tableKey = (string)tableKey;

                    if (str_tableKey[0] == '.') {
                        str_tableKey = str_tableKey.Substring(1);
                    }

                    SortedList list = (SortedList)table[tableKey].GetType().InvokeMember("m_list",
                                                                                BindingFlags.NonPublic |
                                                                                BindingFlags.GetField |
                                                                                BindingFlags.Instance,
                                                                                null,
                                                                                table[tableKey],
                                                                                new object[] { });

                    foreach (var listKey in list.Keys) {
                        String url = "https://" + str_tableKey + (string)listKey;
                        cookieCollection.Add(cookieJar.GetCookies(new Uri(url)));
                    }
                }

                return cookieCollection;
            }
        }

    }
}
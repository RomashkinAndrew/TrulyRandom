﻿using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

// Source: AForge library
namespace TrulyRandom.DirectShow;

internal class FilterInfoCollection : CollectionBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FilterInfoCollection"/> class.
    /// </summary>
    /// <param name="category">Guid of DirectShow filter category</param>
    /// <remarks>Build collection of filters' information objects for the
    /// specified filter category.</remarks>
    public FilterInfoCollection(Guid category)
    {
        CollectFilters(category);
    }

    /// <summary>
    /// Get filter information object.
    /// </summary>
    /// <param name="index">Index of filter information object to retrieve.</param>
    /// <returns>Filter information object.</returns>
    public FilterInfo this[int index] => (FilterInfo)InnerList[index];

    // Collect filters of specified category
    private void CollectFilters(Guid category)
    {
        object comObj = null;
        ICreateDevEnum enumDev;
        IEnumMoniker enumMon = null;
        IMoniker[] devMon = new IMoniker[1];
        int hr;

        try
        {
            // Get the system device enumerator
            Type srvType = Type.GetTypeFromCLSID(Clsid.SystemDeviceEnum);
            if (srvType == null)
            {
                throw new ApplicationException("Failed creating device enumerator");
            }

            // create device enumerator
            comObj = Activator.CreateInstance(srvType);
            enumDev = (ICreateDevEnum)comObj;

            // Create an enumerator to find filters of specified category
            hr = enumDev.CreateClassEnumerator(ref category, out enumMon, 0);
            if (hr != 0)
            {
                throw new ApplicationException("No devices of the category");
            }

            // Collect all filters
            IntPtr n = IntPtr.Zero;
            while (true)
            {
                // Get next filter
                hr = enumMon.Next(1, devMon, n);
                if ((hr != 0) || (devMon[0] == null))
                {
                    break;
                }

                // Add the filter
                FilterInfo filter = new(devMon[0]);
                InnerList.Add(filter);

                // Release COM object
                Marshal.ReleaseComObject(devMon[0]);
                devMon[0] = null;
            }

            // Sort the collection
            InnerList.Sort();
        }
        finally
        {
            // release all COM objects
            if (comObj != null)
            {
                Marshal.ReleaseComObject(comObj);
            }
            if (enumMon != null)
            {
                Marshal.ReleaseComObject(enumMon);
            }
            if (devMon[0] != null)
            {
                Marshal.ReleaseComObject(devMon[0]);
            }
        }
    }
}

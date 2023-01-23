﻿using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using GMap.NET;
using GMap.NET.MapProviders;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using Document = Autodesk.Revit.DB.Document;

namespace cityjsonToRevit
{
    [Transaction(TransactionMode.Manual)]

    class Bag : IExternalCommand
    {
        public List<string> Tiles(string url)
        {
            List<string> tileNums = new List<string>();
            try
            {
                // Create an HttpClient and send the request
                WebClient client = new WebClient();
                string response = client.DownloadString(url);
                dynamic responseJson = JsonConvert.DeserializeObject(response);
                foreach (var feature in responseJson.features)
                {
                    tileNums.Add(feature.properties.tile_id.ToString());
                }
            }

            catch
            {
                TaskDialog.Show("Error", "An error occurred while trying to download the files. Please check your internet connection and try again. ");
            }
            return tileNums;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            UIApplication uiapp = commandData.Application;
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show("Performing on family document", "The plugin should run on project documents.\n");
                return Result.Failed;
            }


            SiteLocation site = doc.ActiveProjectLocation.GetSiteLocation();
            double latDeg = site.Latitude / Program.angleRatio;
            double lonDeg = site.Longitude / Program.angleRatio;
            PointLatLng point = new PointLatLng(latDeg, lonDeg);
            GeoCoderStatusCode geoCoder = GeoCoderStatusCode.Unknow;
            Placemark? placemark = GMapProviders.OpenStreetMap.GetPlacemark(point, out geoCoder);
            if (placemark?.CountryName != "Nederland")
            {
                TaskDialog.Show("Site Loaction out of the Netherlands", "3D BAG service is currently available inside the Netherlands, Please update site location and run the plugin again.");
                return Result.Failed;
            }
            double boxlength = -1;
            using (Command.BagMap bm = new Command.BagMap(latDeg, lonDeg))
            {
                bm.ShowDialog();
                boxlength = bm.side;
            }
            if (boxlength == -1)
            {
                return Result.Failed;
            }
            List<string> tileNums = Tiles("https://data.3dbag.nl/api/BAG3D_v2/wfs?&request=GetFeature&typeName=AG3D_v2:bag_tiles_3k&outputFormat=json&bbox="+ boundingb(latDeg, lonDeg, boxlength));
            if (tileNums.Count == 0)
                return Result.Failed;
            string cjUrl = "https://data.3dbag.nl/cityjson/v210908_fd2cee53/3dbag_v210908_fd2cee53_";
            List<Material> materials = Program.matGenerator(doc);
            string lodSpec = lodBagSelecter();
            if (lodSpec == "")
            {
                return Result.Failed;
            }

            using (Transaction tran = new Transaction(doc, "Build 3D BAG Tiles"))
            {
                tran.Start();
                foreach (string tileNum in tileNums)
                {
                    string cjUrlAll = cjUrl + tileNum + ".json" + ".gz";
                    string gzFile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\TEMP\\" + tileNum + ".gz";
                    if (!File.Exists(gzFile))
                    {
                        try
                        {
                            using (var client2 = new WebClient())
                            {
                                client2.DownloadFile(cjUrlAll, gzFile);
                            }
                        }
                        catch
                        {
                            TaskDialog.Show("Error", "An error occurred while trying to download the files. Please check your internet connection and try again. ");
                            tran.RollBack();
                            return Result.Failed;
                        }

                    }
                    using (FileStream fileToDecompressAsStream = new FileStream(gzFile, FileMode.Open))
                    using (GZipStream decompressionStream = new GZipStream(fileToDecompressAsStream, CompressionMode.Decompress))
                    using (StreamReader sr = new StreamReader(decompressionStream))
                    {
                        string json = sr.ReadToEnd();
                        dynamic jCity = JsonConvert.DeserializeObject(json);
                        List<XYZ> vertList = new List<XYZ>();
                        int epsgNo = Program.epsgNum(jCity);
                        double[] tranC = { jCity.transform.translate[0], jCity.transform.translate[1] };
                        double[] tranR = { lonDeg, latDeg };
                        Program.PointProjectorRev(epsgNo, tranR);
                        double tranx = tranC[0] - tranR[0];
                        double trany = tranC[1] - tranR[1];
                        vertList = Program.vertBuilder(jCity, tranx, trany);
                        //specific 3d bag lod loader for once
                        List<string> paramets = Program.paramFinder(jCity);

                        Dictionary<string, dynamic> semanticParentInfo = new Dictionary<string, dynamic>();

                        foreach (string p in paramets)
                        {
                            Program.paramMaker(uiapp, p);
                        }


                        foreach (var objects in jCity.CityObjects)
                        {
                            foreach (var objProperties in objects)
                            {
                                var attributes = objProperties.attributes;
                                var children = objProperties.children;
                                if (children != null && attributes != null)
                                {
                                    foreach (string child in children)
                                    {
                                        semanticParentInfo.Add(child, attributes);
                                    }
                                }

                            }
                        }



                        foreach (var objects in jCity.CityObjects)
                        {
                            foreach (var objProperties in objects)
                            {
                                string attributeName = objects.Name;
                                string objType = unchecked((string)objProperties.type);


                                Material mat = Program.matSelector(materials, objType, doc);
                                Program.CreateTessellatedShape(doc, mat.Id, objProperties, vertList, attributeName, lodSpec, paramets, semanticParentInfo);
                            }
                        }
                    }
                }
                tran.Commit();
            }
            return Result.Succeeded;

        }
        private string boundingb(double lat, double lon, double a)
        {
            double[] xy = { lon, lat };
            Program.PointProjectorRev(28992, xy);
            double xmax = xy[0] + a;
            double ymax = xy[1] + a;
            double xmin = xy[0] - a;
            double ymin = xy[1] - a;
            string box = xmin.ToString() +","+ ymin.ToString() + "," + xmax.ToString() + "," + ymax.ToString();
            return box;
        }
        private string lodBagSelecter()
        {
            string level = "";
            List<string> lods = new List<string> { "0", "1.2", "1.3", "2.2" };
            using (lodUserSelect loder = new lodUserSelect(lods))
            {
                loder.ShowDialog();
                level = loder._level;
            }
            return level;
        }
    }
}
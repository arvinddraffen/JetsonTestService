using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using JetsonModels;
using JetsonModels.Database;
using Microsoft.EntityFrameworkCore;
using RestSharp;
using System.Linq;
using Newtonsoft.Json;
using System.Net;
using System.Threading;

public class UpdateMessage
{
    public uint CID { get; set; }   // Cluster ID
    public uint NID { get; set; }   // Node ID
    public uint freemem { get; set; }   // MB
    public uint usedmem { get; set; }   // MB
    public String NIP { get; set; }  // IPv4 address
    public float[] cpuutil { get; set; }    // %
    public String OS { get; set; }   // name of operating system
    public TimeSpan utime { get; set; } // uptime of the node
    public int frequency { get; set; }   //Hz
}

namespace JetsonService
{
    /// <summary>
    /// <see cref="Program"/> is the main entry point into JetsonService.
    /// </summary>
    internal class Program
    {
        static string[] NodeIPs;

        private static void Init()
        {
            string ConfigFile = System.IO.File.ReadAllText(@"JetsonServiceConfig.txt");
            NodeIPs = ConfigFile.Split(new Char[] { '\n' });
        }

        private static void ReceiveMessage(int index)
        {
            var optionsBuilder = new DbContextOptionsBuilder<JetsonModels.Context.ClusterContext>();
            optionsBuilder.UseSqlite("Data Source=/var/lib/jetson/data.db");

            var options = optionsBuilder.Options;

            JetsonModels.Context.ClusterContext database = new JetsonModels.Context.ClusterContext(options);

            int frequency = 1;
            while (true)
            {
                Thread.Sleep(1000 / frequency);

                var client = new RestClient("http://" + NodeIPs[index]);
                var request = new RestRequest("/nodeupdate", Method.GET);
                UpdateMessage myMessage;
                do
                {
                    var content = client.Execute(request).Content;
                    myMessage = JsonConvert.DeserializeObject<UpdateMessage>(content);
                }
                while (myMessage == null);

                frequency = myMessage.frequency;

                // Find cluster with received Id. If not exists, create new cluster
                var cluster = database.Clusters
                    .Include(c => c.Nodes)
                    .FirstOrDefault(c => c.Id == myMessage.CID);

                if (cluster == null)
                {
                    cluster = new Cluster();
                    cluster.Id = myMessage.CID;
                    cluster.Nodes = new List<Node>();
                    cluster.RefreshRate = TimeSpan.FromMilliseconds(1000/frequency);
                    cluster.Type = Cluster.ClusterType.Jetson;
                    cluster.ClusterName = "Jetson 2.0";
                    database.Clusters.Add(cluster);
                }

                //// Find in cluster with given node Id. If not exists, create new node
                var node = cluster.Nodes.FirstOrDefault(n => n.Id == myMessage.NID);

                if (node == null)
                {
                    node = new Node()
                    {
                        Id = myMessage.NID,
                        IPAddress = myMessage.NIP,
                        OperatingSystem = myMessage.OS,
                        UpTime = myMessage.utime,
                    };
                    cluster.Nodes.Add(node);
                }

                database.SaveChanges();

                List<CpuCore> myCores = new List<CpuCore>();

                for (uint j = 0; j < myMessage.cpuutil.Length; j++)
                {
                    myCores.Add(new CpuCore() { CoreNumber = j, UtilizationPercentage = myMessage.cpuutil[j] });
                }

                // Add utilization information for the node
                database.UtilizationData.Add(new NodeUtilization()
                {
                    Id = myMessage.NID,
                    TimeStamp = DateTime.Now,
                    Cores = myCores,
                    MemoryAvailable = myMessage.freemem,
                    MemoryUsed = myMessage.usedmem,
                    GlobalNodeId = node.GlobalId,
                });

                int i = new Random().Next(1, 10);

                database.PowerData.Add(new NodePower()
                {
                    GlobalNodeId = node.GlobalId,
                    Timestamp = DateTime.Now,
                    Current = (i / 3F) % 744,
                    Voltage = (i / 4F) % 4,
                    Power = (i / 1000F) * (i / 2000F),
                });

                database.SaveChanges();
                
            }
        }

        private static void Main(string[] args)
        {
            Init();

            var optionsBuilder = new DbContextOptionsBuilder<JetsonModels.Context.ClusterContext>();
            optionsBuilder.UseSqlite("Data Source=/var/lib/jetson/data.db");

            var options = optionsBuilder.Options;

            JetsonModels.Context.ClusterContext database = new JetsonModels.Context.ClusterContext(options);

            Task[] myTasks = new Task[NodeIPs.Length];
            for (int i = 0; i < NodeIPs.Length; i++)
            {
                myTasks[i] = Task.Factory.StartNew(o => ReceiveMessage((int)o), i);
            }

            while (true)
            {
                DateTime now = DateTime.Now;
                var weekOldNodeUtils = database.UtilizationData.Where(x => now.Ticks - x.TimeStamp.Ticks >= new TimeSpan(7, 0, 0, 0).Ticks);
                var weekOldPowerData = database.PowerData.Where(x => now.Ticks - x.Timestamp.Ticks >= new TimeSpan(7, 0, 0, 0).Ticks);
                foreach (NodeUtilization weekOldNodeUtil in weekOldNodeUtils)
                {
                    database.UtilizationData.Remove(weekOldNodeUtil);
                }
                foreach (NodePower weekOldPowerDatum in weekOldPowerData)
                {
                    database.PowerData.Remove(weekOldPowerDatum);
                }
                database.SaveChanges();
            }

            Task.WaitAll(myTasks);
        }
    }
}

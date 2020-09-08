﻿using Amazon.S3.Model;
using Microsoft.Win32;
using Newtonsoft.Json;
using SW.CloudFiles;
using SW.PrimitiveTypes;
using SW.Serverless.Installer.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace SW.Serverless.Desktop
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        private IDictionary<string, CloudConnection> connections;
        private CloudConnection chosenConnection;
        private string chosenAdapterPath;
        private Options options;
        private InstallerLogic installer;
        public MainWindow()
        {
            InitializeComponent();
            installer = new InstallerLogic();
            initConnections();
        }

        private void addConnection(CloudConnection con)
        {
            var key = $"{con.BucketName}.{con.ServiceUrl}";
            if (connections.TryAdd(key, con))
            {
                connectionListBox.Items.Add(new ListBoxItem { Content = key });
            }
        }

        private void initConnections()
        {
            this.options = GetOptionsFromJson();
            connections = new Dictionary<string, CloudConnection>();
            foreach(var con in options.CloudConnections)
                addConnection(con);
        }

        private void chooseConnection(object sender, RoutedEventArgs e)
        {
            var item = (ListBoxItem)connectionListBox.SelectedItem;
            string key = item.Content.ToString();
            
            chosenConnection = connections[key];
        }
        private void addConnectionToJson(object sender, RoutedEventArgs e)
        {

            Options current = GetOptionsFromJson();
            var connection = new CloudConnection
            {
                AccessKeyId = accessKeyText.Text,
                BucketName = bucketNameText.Text,
                SecretAccessKey = secretAccessText.Text,
                ServiceUrl = serviceUrlText.Text
            };
            current.CloudConnections.Add(connection);
            string optionsJson = JsonConvert.SerializeObject(current);
            File.WriteAllText("./settings.json", optionsJson);
            addConnection(connection);

        }

        private Options GetOptionsFromJson()
        {
            if (File.Exists("./settings.json"))
            {
                string optionsJson = File.ReadAllText("./settings.json");
                return JsonConvert.DeserializeObject<Options>(optionsJson);
            }
            else
            {
                File.WriteAllText("./settings.json", JsonConvert.SerializeObject(new Options()));
                return new Options();
            }
            
        }

        private async void installAdapter(object sender, RoutedEventArgs e)
        {
            string projectPath = chosenAdapterPath;
            if(chosenConnection == null)
            {
                throw new Exception("Invalid connection");
            }

            var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

            if (!installer.BuildPublish(projectPath, tempPath)) return;

            string adapterId = adapterIdText.Text;

            var zipFileName = System.IO.Path.Combine(tempPath, $"{adapterId}");

            if (!installer.Compress(tempPath, zipFileName)) return;

            var projectFileName = System.IO.Path.GetFileName(projectPath);
            var entryAssembly = $"{projectFileName.Remove(projectFileName.LastIndexOf('.'))}.dll";

            if (await installer.PushToCloudAsync(zipFileName, adapterId, entryAssembly, chosenConnection.AccessKeyId, chosenConnection.SecretAccessKey, chosenConnection.ServiceUrl, chosenConnection.BucketName))
            {
                installButton.Content = "Install successful. You can install another adapter.";
            }
            else
            {
                installButton.Content = "Install failed, check configuration.";
            }


            if (!installer.Cleanup(tempPath)) return;

        }

        private void chooseAdapter(object sender, RoutedEventArgs args)
        {
            var dialogue = new OpenFileDialog();
            dialogue.Multiselect = false;
            dialogue.CheckFileExists = true;
            dialogue.ValidateNames = true;
            dialogue.ShowDialog();
            if(dialogue.FileNames != null && dialogue.FileNames.Length > 0)
                chosenAdapterPath = dialogue.FileName;
        }

    }
}

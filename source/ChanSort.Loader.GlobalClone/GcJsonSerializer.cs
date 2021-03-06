﻿using System;
using System.IO;
using System.Text;
using ChanSort.Api;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChanSort.Loader.GlobalClone
{
  internal class GcJsonSerializer : SerializerBase
  {
    private readonly string content;
    string xmlPrefix;
    string xmlSuffix;
    private JObject doc;

    //private readonly ChannelList tvList = new ChannelList(SignalSource.MaskAdInput | SignalSource.Tv, "TV");
    //private readonly ChannelList radioList = new ChannelList(SignalSource.MaskAdInput | SignalSource.Radio, "Radio");

    public GcJsonSerializer(string filename, string content) : base(filename)
    {
      this.content = content;

      this.Features.DeleteMode = DeleteMode.FlagWithoutPrNr;
      this.Features.ChannelNameEdit = ChannelNameEditMode.All;
      this.Features.SupportedFavorites = 0;
      this.Features.CanSaveAs = true;
      this.Features.CanHaveGaps = true;
      this.Features.CanHideChannels = true;
      this.Features.CanSkipChannels = true;
      this.Features.CanLockChannels = true;

      this.DataRoot.AddChannelList(new ChannelList(SignalSource.AnalogT  | SignalSource.Tv | SignalSource.Data, "Analog Antenna"));
      this.DataRoot.AddChannelList(new ChannelList(SignalSource.DvbT | SignalSource.Tv | SignalSource.Data, "DVB-T TV"));
      this.DataRoot.AddChannelList(new ChannelList(SignalSource.DvbT | SignalSource.Radio, "DVB-T Radio"));
      this.DataRoot.AddChannelList(new ChannelList(SignalSource.AnalogC | SignalSource.Tv | SignalSource.Data, "Analog Cable"));
      this.DataRoot.AddChannelList(new ChannelList(SignalSource.DvbC | SignalSource.Tv | SignalSource.Data, "DVB-C TV"));
      this.DataRoot.AddChannelList(new ChannelList(SignalSource.DvbC | SignalSource.Radio, "DVB-C Radio"));
      this.DataRoot.AddChannelList(new ChannelList(SignalSource.DvbS | SignalSource.Tv | SignalSource.Data, "DVB-S TV"));
      this.DataRoot.AddChannelList(new ChannelList(SignalSource.DvbS | SignalSource.Radio, "DVB-S Radio"));
      //this.DataRoot.AddChannelList(tvList);
      //this.DataRoot.AddChannelList(radioList);

      //foreach(var list in this.DataRoot.ChannelLists)
      //  list.VisibleColumnFieldNames.Add("Source");
    }


    public override void Load()
    {
      var startTag = "<legacybroadcast>";
      var endTag = "</legacybroadcast>";
      var start = content.IndexOf(startTag);
      string json = null;
      if (start >= 0)
      {
        this.xmlPrefix = content.Substring(0, start + startTag.Length);
        var end = content.IndexOf(endTag, start);
        if (end >= 0)
        {
          json = content.Substring(start + startTag.Length, end - start - startTag.Length);
          this.xmlSuffix = content.Substring(end);
        }
      }

      if (json == null)
        throw new FileLoadException($"File does not contain a {startTag}...{endTag} node");

      this.doc = JObject.Parse(json);
      LoadSatellites();
      LoadChannels();
    }

    #region LoadSatellites()
    private void LoadSatellites()
    {
      var satList = this.doc["satelliteList"];
      if (satList == null)
        return;
      foreach (var node in satList)
      {
        if (!(bool) node["tpListLoad"])
          continue;
        var id = int.Parse((string) node["satelliteId"]);
        var sat = new Satellite(id);
        sat.Name = (string) node["satelliteName"];
        sat.OrbitalPosition = (string) node["satLocation"];
        this.DataRoot.AddSatellite(sat);

        LoadTransponders(node["TransponderList"], sat);
      }
    }
    #endregion

    #region LoadTransponders()
    private void LoadTransponders(JToken transponderList, Satellite sat)
    {
      foreach (var node in transponderList)
      {
        var id = (sat.Id << 16) + (int)node["channelIdx"];
        var tp = new Transponder(id);
        tp.Satellite = sat;
        sat.Transponder.Add(id, tp);

        tp.FrequencyInMhz = (int) node["frequency"];
        tp.Number = (int) node["channelIdx"];
        var pol = ((string) node["polarization"]).ToLower();
        tp.Polarity = pol.StartsWith("h") ? 'H' : pol.StartsWith("V") ? 'V' : '\0';
        tp.TransportStreamId = (int) node["TSID"];
        tp.OriginalNetworkId = (int)node["ONID"];
        tp.SymbolRate = (int) node["symbolRate"];
      }
    }
    #endregion

    #region LoadChannels()
    private void LoadChannels()
    {
      if (this.doc["channelList"] == null)
        throw new FileLoadException("JSON does not contain a channelList node");

      var dec = new DvbStringDecoder(this.DefaultEncoding);
      int i = 0;
      foreach (var node in this.doc["channelList"])
      {
        var ch = new GcChannel<JToken>(0, i, node);

        ch.Source = (string)node["sourceIndex"];
        if (ch.Source == "SATELLITE DIGITAL")
          ch.SignalSource |= SignalSource.DvbS;
        else if (ch.Source == "CABLE DIGITAL")
          ch.SignalSource |= SignalSource.DvbC;
        else if (ch.Source.Contains("ANTENNA DIGITAL"))
          ch.SignalSource |= SignalSource.DvbT;
        else if (ch.Source.Contains("ANTENNA ANALOG"))
          ch.SignalSource |= SignalSource.AnalogT;
        else if (ch.Source.Contains("CABLE ANALOG"))
          ch.SignalSource |= SignalSource.AnalogC;
        else
        {
          // TODO: add some log for skipped channels
          continue;
        }

        ch.IsDisabled = (bool) node["disabled"];
        ch.Skip = (bool) node["skipped"];
        ch.Lock = (bool)node["locked"];
        ch.Hidden = (bool) node["Invisible"];
        var nameBytes = Convert.FromBase64String((string) node["chNameBase64"]);
        dec.GetChannelNames(nameBytes, 0, nameBytes.Length, out var name, out var shortName);
        ch.Name = name;
        ch.ShortName = shortName;

        ch.OldProgramNr = ch.IsDeleted ? -1 : (int) node["majorNumber"];
        if (string.IsNullOrWhiteSpace(ch.Name))
          ch.Name = (string)node["channelName"];

        ch.TransportStreamId = (int)node["TSID"];

        if ((ch.SignalSource & SignalSource.Digital) != 0)
        {
          var transSystem = (string) node["transSystem"];

          //if (int.TryParse((string) node["satelliteId"], out var satId))
          ch.Satellite = (string) node["satelliteId"]; //this.DataRoot.Satellites.TryGet(satId);
          ch.Encrypted = (bool) node["scrambled"];
          ch.FreqInMhz = (int) node["frequency"];
          if (ch.FreqInMhz >= 100000 && ch.FreqInMhz < 1000000) // DVBS is given in MHz, DVBC/T in kHz
            ch.FreqInMhz /= 1000;

          var tpId = (string) node["tpId"];
          if (tpId != null && tpId.Length == 10)
            ch.Transponder = this.DataRoot.Transponder.TryGet((int.Parse(tpId.Substring(0, 4)) << 16) + int.Parse(tpId.Substring(4))); // satId + freq, e.g. 0192126041

          ch.IsDeleted = (bool) node["deleted"];
          ch.PcrPid = (int) node["pcrPid"];
          ch.AudioPid = (int) node["audioPid"];
          ch.VideoPid = (int) node["videoPid"];
          ch.ServiceId = (int) node["SVCID"];
          if (ch.ServiceId == 0)
            ch.ServiceId = (int) node["programNum"];
          ch.ServiceType = (int) node["serviceType"];
          ch.OriginalNetworkId = (int) node["ONID"];
          ch.SignalSource |= LookupData.Instance.IsRadioTvOrData(ch.ServiceType);
        }
        else
        {
          ch.ChannelOrTransponder = (string) node["TSID"];
          ch.SignalSource |= SignalSource.Tv;
        }


        if ((ch.OldProgramNr & 0x4000) != 0)
        {
          ch.OldProgramNr &= 0x3FFF;
          ch.SignalSource |= SignalSource.Radio;
        }
        
        var list = this.DataRoot.GetChannelList(ch.SignalSource);
        this.DataRoot.AddChannel(list, ch);
      }
    }
    #endregion

    // saving

    #region Save()
    public override void Save(string tvOutputFile)
    {
      this.UpdateJsonDoc();

      using var sw = new StringWriter();
      sw.Write(xmlPrefix);
      var jw = new JsonTextWriter(sw);
      {
        doc.WriteTo(jw);
      }
      sw.Write(xmlSuffix);
      File.WriteAllText(tvOutputFile, sw.ToString());
      this.FileName = tvOutputFile;
    }
    #endregion

    #region UpdateJsonDoc()
    private void UpdateJsonDoc()
    {
      foreach (var list in this.DataRoot.ChannelLists)
      {
        int radioMask = (list.SignalSource & SignalSource.Radio) != 0 ? 0x4000 : 0;
        foreach (var chan in list.Channels)
        {
          if (!(chan is GcChannel<JToken> ch))
            continue;
          var node = ch.Node;
          if (ch.IsNameModified)
          {
            node["channelName"] = ch.Name;
            node["chNameBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(ch.Name));
          }

          node["deleted"] = ch.IsDeleted;
          var nr = Math.Max(ch.NewProgramNr, 0); // radio channels, except the deleted ones with nr=0, have 0x4000 added to their number
          if (nr != 0)
            nr |= radioMask;
          node["majorNumber"] = nr;
          node["skipped"] = ch.Skip;
          node["locked"] = ch.Lock;
          node["Invisible"] = ch.Hidden;
          if (ch.NewProgramNr != ch.OldProgramNr)
          {
            node["userEditChNumber"] = true;
            node["userSelCHNo"] = true;
          }

          //node["disableUpdate"] = true; // experimental to prevent "DTV Auto Update" of channel numbers right after importing the list
        }
      }
    }
    #endregion
  }

}

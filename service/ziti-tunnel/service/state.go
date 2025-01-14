/*
 * Copyright NetFoundry, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * https://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
 */

package service

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"github.com/openziti/desktop-edge-win/service/cziti"
	"github.com/openziti/desktop-edge-win/service/windns"
	"github.com/openziti/desktop-edge-win/service/ziti-tunnel/config"
	"github.com/openziti/desktop-edge-win/service/ziti-tunnel/constants"
	"github.com/openziti/desktop-edge-win/service/ziti-tunnel/dto"
	"github.com/openziti/foundation/identity/identity"
	idcfg "github.com/openziti/sdk-golang/ziti/config"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
	"golang.zx2c4.com/wireguard/tun"
	"golang.zx2c4.com/wireguard/tun/wintun"
	"golang.zx2c4.com/wireguard/windows/tunnel/winipcfg"
	"io"
	"io/ioutil"
	"net"
	"os"
	"path"
	"strconv"
	"strings"
	"sync/atomic"
	"time"
)

type RuntimeState struct {
	state     *dto.TunnelStatus
	tun       *tun.Device
	tunName   string
	ids       map[string]*Id
	tun_state atomic.Value
}

func (t *RuntimeState) RemoveByFingerprint(fingerprint string) {
	delete(t.ids, fingerprint)
}

func (t *RuntimeState) Find(fingerprint string) *Id {
	return t.ids[fingerprint]
}

func (t *RuntimeState) SaveState() {
	// overwrite file if it exists
	_ = os.MkdirAll(config.Path(), 0644)

	log.Debugf("backing up config")
	backup, err := backupConfig()
	if err != nil {
		log.Warnf("could not backup config file! %v", err)
	} else {
		log.Debugf("config file backed up to: %s", backup)
	}

	cfg, err := os.OpenFile(config.File(), os.O_RDWR|os.O_CREATE|os.O_TRUNC, 0644)
	defer cfg.Close()
	if err != nil {
		log.Panicf("An unexpected and unrecoverable error has occurred while %s: %v", "opening the config file", err)
	}

	w := bufio.NewWriter(bufio.NewWriter(cfg))
	enc := json.NewEncoder(w)
	enc.SetIndent("", "  ")
	_ = enc.Encode(t.ToStatus(false))
	_ = w.Flush()

	err = cfg.Close()
	if err != nil {
		log.Panicf("An unexpected and unrecoverable error has occurred while %s: %v", "closing the config file", err)
	}
	log.Debug("state saved")
}

func backupConfig() (string, error) {
	original, err := os.Open(config.File())
	if err != nil {
		return "", err
	}
	defer original.Close()
	backup := config.File() + ".backup"
	new, err := os.Create(backup)
	if err != nil {
		return "", err
	}
	defer new.Close()

	_, err = io.Copy(new, original)
	if err != nil {
		return "", err
	}
	return backup, err
}

func (t *RuntimeState) ToStatus(onlyInitialized bool) dto.TunnelStatus {
	var uptime int64

	now := time.Now()
	tunStart := now.Sub(TunStarted)
	uptime = tunStart.Milliseconds()

	clean := dto.TunnelStatus{
		Active:                t.state.Active,
		Duration:              uptime,
		Identities:            make([]*dto.Identity, 0),
		IpInfo:                t.state.IpInfo,
		LogLevel:              t.state.LogLevel,
		ServiceVersion:        Version,
		TunIpv4:               t.state.TunIpv4,
		TunIpv4Mask:           t.state.TunIpv4Mask,
		AddDns:                t.state.AddDns,
		NotificationFrequency: t.state.NotificationFrequency,
		ApiPageSize:           t.state.ApiPageSize,
	}

	i := 0
	for _, id := range t.ids {
		if onlyInitialized {
			if id.CId != nil && id.CId.Loaded {
				cid := Clean(id)
				clean.Identities = append(clean.Identities, &cid)
			}
		} else {
			cid := Clean(id)
			clean.Identities = append(clean.Identities, &cid)
		}
		i++
	}

	return clean
}

func (t *RuntimeState) ToMetrics() dto.TunnelStatus {
	clean := dto.TunnelStatus{
		Identities: make([]*dto.Identity, len(t.ids)),
	}

	i := 0
	for _, id := range t.ids {
		AddMetrics(id)
		clean.Identities[i] = &dto.Identity{
			Name:               id.Name,
			FingerPrint:        id.FingerPrint,
			Metrics:            id.Metrics,
			Active:             id.Active,
			MfaEnabled:         id.MfaEnabled,
			MfaNeeded:          id.MfaNeeded,
			MfaMinTimeout:      id.MfaMinTimeout,
			MfaMaxTimeout:      id.MfaMaxTimeout,
			MfaMinTimeoutRem:   id.MfaMinTimeoutRem,
			MfaMaxTimeoutRem:   id.MfaMaxTimeoutRem,
			MfaLastUpdatedTime: id.MfaLastUpdatedTime,
		}
		i++
	}

	return clean
}

func (t *RuntimeState) CreateTun(ipv4 string, ipv4mask int, applyDns bool) (net.IP, *tun.Device, error) {
	log.Infof("creating TUN device: %s", TunName)
	tunDevice, err := tun.CreateTUN(TunName, 64*1024-1)
	if err == nil {
		t.tun = &tunDevice
		tunName, err2 := tunDevice.Name()
		if err2 == nil {
			t.tunName = tunName
		}
	} else {
		return nil, nil, fmt.Errorf("error creating TUN device: (%v)", err)
	}

	if name, err := tunDevice.Name(); err == nil {
		log.Debugf("created TUN device [%s]", name)
	} else {
		return nil, nil, fmt.Errorf("error getting TUN name: (%v)", err)
	}

	nativeTunDevice := tunDevice.(*tun.NativeTun)
	luid := winipcfg.LUID(nativeTunDevice.LUID())

	if strings.TrimSpace(ipv4) == "" {
		log.Infof("ip not provided using default: %v", ipv4)
		ipv4 = constants.Ipv4ip
		rts.UpdateIpv4(ipv4)
	}
	if ipv4mask < constants.Ipv4MaxMask {
		log.Warnf("provided mask is too large: %d using default: %d", ipv4mask, constants.Ipv4DefaultMask)
		ipv4mask = constants.Ipv4DefaultMask
		rts.UpdateIpv4Mask(ipv4mask)
	}
	ip, ipnet, err := net.ParseCIDR(fmt.Sprintf("%s/%d", ipv4, ipv4mask))
	if err != nil {
		return nil, nil, fmt.Errorf("error parsing CIDR block: (%v)", err)
	}

	log.Infof("setting TUN interface address to [%s]", ip)
	err = luid.SetIPAddresses([]net.IPNet{{IP: ip, Mask: ipnet.Mask}})
	if err != nil {
		return nil, nil, fmt.Errorf("failed to set IP address to %v: (%v)", ip, err)
	}

	log.Info("checking TUN dns servers")
	dns, err := luid.DNS()
	if err != nil {
		return nil, nil, fmt.Errorf("failed to fetch DNS address: (%v)", err)
	}
	log.Infof("TUN dns servers set to: %s", dns)

	log.Infof("setting routes for cidr: %s. Next Hop: %s", ipnet.String(), ipnet.IP.String())
	err = luid.SetRoutes([]*winipcfg.RouteData{{Destination: *ipnet, NextHop: ipnet.IP, Metric: 0}})
	if err != nil {
		return nil, nil, fmt.Errorf("failed to SetRoutes: (%v)", err)
	}
	log.Info("routing applied")

	zitiPoliciesEffective := windns.IsNrptPoliciesEffective(ipv4)
	interfaceMetric := 255
	if applyDns || !zitiPoliciesEffective {
		if applyDns {
			log.Infof("DNS is applied to the TUN interface, because apply Dns flag in the config file is %t ", applyDns)
		}
		if !applyDns && !zitiPoliciesEffective {
			log.Infof("DNS is applied to the TUN interface, because Ziti policies test result in this client is %t", zitiPoliciesEffective)
		}
		//for windows 10+, could 'domains' be able to replace NRPT? dunno - didn't test it
		luid.SetDNS(windows.AF_INET, []net.IP{ip}, nil)
		interfaceMetric = 5
	}
	cziti.SetInterfaceMetric(TunName, interfaceMetric)
	log.Debugf("Interface Metric of %s is set to %d", TunName, interfaceMetric)

	return ip, t.tun, nil
}

func (t *RuntimeState) LoadIdentity(id *Id, refreshInterval int) {
	if id.CId != nil && id.CId.Loaded {
		log.Warnf("id %s[%s] already connected", id.Name, id.FingerPrint)
		return
	}

	_, err := os.Stat(id.Path())
	if err != nil {
		if os.IsNotExist(err) {
			//file does not exist. TODO remove this from the list
		} else {
			log.Warnf("refusing to load identity with fingerprint %s:%s due to error %v", id.Name, id.FingerPrint, err)
		}
		return
	}

	log.Infof("loading identity %s[%s]", id.Name, id.FingerPrint)

	sc := func(status int) {
		log.Tracef("identity status change! %d", status)
		id.ControllerVersion = id.CId.Version
		id.CId.Fingerprint = id.FingerPrint
		id.CId.Loaded = true
		id.Config.ZtAPI = id.CId.Controller()

		// hack for now - if the identity name is '<unknown>' don't set it... :(
		if id.CId.Name == "<unknown>" || id.CId.Name == "" {
			log.Debugf("name is set to '%s' which probably indicates the controller is down or the identity is not authorized - not changing the name. Continuing to use: %s", id.CId.Name, id.Name)
		} else if id.Name != id.CId.Name {
			log.Debugf("name changed from %s to %s", id.Name, id.CId.Name)
			id.Name = id.CId.Name
			rts.SaveState()
		}
		log.Infof("successfully loaded %s@%s", id.CId.Name, id.CId.Controller())

		id.Config.ID = identity.IdentityConfig{} //after successfully loading the identity clear the id info

		_, found := t.ids[id.FingerPrint]
		if !found {
			t.ids[id.FingerPrint] = id //add this identity to the list of known ids
		}
		id.MfaEnabled = id.CId.MfaEnabled
		id.MfaNeeded = id.CId.MfaNeeded

		rts.BroadcastEvent(dto.IdentityEvent{
			ActionEvent: dto.IDENTITY_ADDED,
			Id:          id.Identity,
		})
		log.Infof("connecting identity completed: %s[%s] %t/%t", id.Name, id.FingerPrint, id.MfaEnabled, id.MfaNeeded)
	}

	id.CId = cziti.NewZid(sc)
	id.CId.Active = id.Active
	log.Debugf("Default API PAGE SIZE set to: %d", rts.state.ApiPageSize)
	cziti.LoadZiti(id.CId, id.Path(), refreshInterval, rts.state.ApiPageSize)
}

func (t *RuntimeState) LoadConfig() {
	scanForIdentitiesPostWindowsUpdate()
	err := readConfig(t, config.File())
	if err != nil {
		err = readConfig(t, config.BackupFile())
		if err != nil {
			//this means BOTH files are unusable. that's really bad... :( delete both files and then panic...
			os.Remove(config.File())
			os.Remove(config.BackupFile())
			log.Panicf("config file is not valid nor is backup file! both files have been deleted.")
		}
	}

	//find/fix orphaned identities
	t.scanForOrphanedIdentities(config.Path())

	//any specific code needed when starting the process. some values need to be cleared
	TunStarted = time.Now() //reset the time on startup

	if t.state.TunIpv4Mask > constants.Ipv4MinMask {
		log.Warnf("provided mask: [%d] is smaller than the minimum permitted: [%d] and will be changed", rts.state.TunIpv4Mask, constants.Ipv4MinMask)
		rts.UpdateIpv4Mask(constants.Ipv4MinMask)
	}

	if t.state.NotificationFrequency < constants.MinimumFrequency {
		rts.UpdateNotificationFrequency(constants.MinimumFrequency)
	}
}

func (t *RuntimeState) scanForOrphanedIdentities(folder string) {
	files, err := ioutil.ReadDir(folder)
	if err != nil {
		log.Panic(err)
	}
	for _, f := range files {
		if strings.HasSuffix(f.Name(), "json") {
			cfg := idcfg.Config{}
			err = probeIdentityFile(path.Join(folder, f.Name()), &cfg)
			if err != nil {
				log.Tracef("file is not deserializable as a config file. probably config.json etc.%s", f.Name())
				continue
			}
			if strings.TrimSpace(cfg.ID.Key) != "" {
				log.Debugf("Config file appears to be valid for network: %s", cfg.ZtAPI)
				fingerprint := strings.Split(f.Name(), ".")[0]
				var found *dto.Identity
				for _, sid := range t.state.Identities {
					if sid.FingerPrint == fingerprint {
						found = sid
						break
					}
				}
				if found == nil {
					log.Infof("found orphaned identity %s. Adding back to the configuration", fingerprint)
					newId := dto.Identity{
						Name:        "recovered identity",
						FingerPrint: fingerprint,
						Active:      false,
						Config:      cfg,
					}

					t.state.Identities = append(t.state.Identities, &newId)
				} else {
					log.Debugf("identity with fingerprint is known: %s", fingerprint)
				}
			} else {
				log.Debugf("json file %s does not appear to be an identity", f.Name())
			}
		}
	}
}

func probeIdentityFile(path string, cfg *idcfg.Config) error {
	file, err := os.OpenFile(path, os.O_RDONLY, 0644)
	if err != nil {
		log.Errorf("unexpected error opening config file: %v", err)
	}

	r := bufio.NewReader(file)
	dec := json.NewDecoder(r)
	err = dec.Decode(&cfg)
	defer file.Close()
	return err
}

func readConfig(t *RuntimeState, filename string) error {
	log.Infof("reading config file located at: %s", filename)
	info, err := os.Stat(filename)
	if os.IsNotExist(err) {
		log.Infof("the config file does not exist. this is normal if this is a new install or if the config file was removed manually")
		rts.state = &dto.TunnelStatus{}
		return nil
	}

	if info.Size() == 0 {
		return fmt.Errorf("the config file at contains no bytes and is considered invalid: %s", filename)
	}

	file, err := os.OpenFile(filename, os.O_RDONLY, 0644)
	if err != nil {
		return fmt.Errorf("unexpected error opening config file: %v", err)
	}

	r := bufio.NewReader(file)
	dec := json.NewDecoder(r)

	err = dec.Decode(&t.state)
	defer file.Close()

	if err != nil {
		return fmt.Errorf("unexpected error reading config file: %v", err)
	}
	return nil
}

func (t *RuntimeState) UpdateIpv4Mask(ipv4mask int) {
	rts.state.TunIpv4Mask = ipv4mask
	rts.SaveState()
}
func (t *RuntimeState) UpdateIpv4(ipv4 string) {
	rts.state.TunIpv4 = ipv4
	rts.SaveState()
}

func UpdateRuntimeStateIpv4(ip string, ipv4Mask int, addDns string, apiPageSize int) error {

	log.Infof("updating configuration ip: %s, mask: %d, dns: %t, apiPageSize: %d", ip, ipv4Mask, addDns, apiPageSize)

	if ipv4Mask < constants.Ipv4MaxMask || ipv4Mask > constants.Ipv4MinMask {
		return errors.New(fmt.Sprintf("ipv4Mask should be between %d and %d", constants.Ipv4MaxMask, constants.Ipv4MinMask))
	}

	if addDns != "" {
		addDnsBool, err := strconv.ParseBool(addDns)

		if err != nil {
			return errors.New(fmt.Sprintf("Incorrect addDns %v", err))
		}

		rts.state.AddDns = addDnsBool
	}

	// if ip is not empty, then we set both ip and mask
	if ip != "" {
		rts.state.TunIpv4 = ip
		rts.state.TunIpv4Mask = ipv4Mask
	}

	rts.state.ApiPageSize = apiPageSize

	rts.SaveState()

	return nil
}

// uses the registry to determine if IPv6 is enabled or disabled on this machine. If it is disabled an IPv6 DNS entry
// will end up causing a fatal error on startup of the service. For this registry key and values see the MS documentation
// at https://docs.microsoft.com/en-us/troubleshoot/windows-server/networking/configure-ipv6-in-windows
func iPv6Disabled() bool {
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, `SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters`, registry.QUERY_VALUE)
	if err != nil {
		log.Warnf("could not read registry to detect IPv6 - assuming IPv6 enabled. If IPv6 is not enabled the service may fail to start")
		return false
	}
	defer k.Close()

	val, _, err := k.GetIntegerValue("DisabledComponents")
	if err != nil {
		log.Debugf("registry key HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters\\DisabledComponents not present. IPv6 is enabled")
		return false
	}
	actual := val & 255
	log.Debugf("read value from registry: %d. using actual: %d", val, actual)
	if actual == 255 {
		return true
	} else {
		log.Infof("IPv6 has DisabledComponents set to %d. If the service fails to start please report this message", val)
		return false
	}
}

func (t *RuntimeState) AddRoute(destination net.IPNet, nextHop net.IP, metric uint32) error {
	nativeTunDevice := (*t.tun).(*tun.NativeTun)
	luid := winipcfg.LUID(nativeTunDevice.LUID())
	return luid.AddRoute(destination, nextHop, metric)
}

func (t *RuntimeState) RemoveRoute(destination net.IPNet, nextHop net.IP) error {
	nativeTunDevice := (*t.tun).(*tun.NativeTun)
	luid := winipcfg.LUID(nativeTunDevice.LUID())
	return luid.DeleteRoute(destination, nextHop)
}

func (t *RuntimeState) Close() {
	val := t.tun_state.Load()
	if val != nil {
		log.Debugf("Tun is closing or is already closed!")
		return
	}
	t.tun_state.Store("closing")
	if t.tun != nil {
		tu := *t.tun
		log.Infof("Closing native tun: %s", TunName)
		err := tu.Close()

		if err != nil {
			log.Error("problem closing tunnel!")
		} else {
			t.tun = nil
			log.Infof("Closed native tun: %s", TunName)
		}
	} else {
		log.Warn("unexpected situation. the TUN was null? ")
	}
	t.RemoveZitiTun()
}

func (t *RuntimeState) RemoveZitiTun() {
	log.Infof("Removing existing interface: %s", TunName)
	wt, err := tun.WintunPool.OpenAdapter(TunName)
	if err == nil {
		// If so, we delete it, in case it has weird residual configuration.
		_, err = wt.Delete(true)
		if err != nil {
			log.Errorf("Error deleting already existing interface: %v", err)
		} else {
			log.Infof("Removed wintun tun: %s", TunName)
		}
	} else {
		log.Tracef("INTERFACE %s was nil? must have been removed already. %v", TunName, err)
	}
	log.Infof("Successfully removed interface: %s", TunName)
}

func (t *RuntimeState) InterceptDNS() {
	log.Panicf("implement me")
}

func (t *RuntimeState) ReleaseDNS() {
	log.Panicf("implement me")
}

func (t *RuntimeState) InterceptIP() {
	log.Panicf("implement me")
}

func (t *RuntimeState) ReleaseIP() {
	log.Panicf("implement me")
}

func (t *RuntimeState) BroadcastEvent(event interface{}) {
	if len(events.broadcast) == cap(events.broadcast) {
		log.Warn("event channel is full and is about to block!")
	}
	events.broadcast <- event
}

func (t *RuntimeState) UpdateMfa(fingerprint string, mfaEnabled bool, mfaNeeded bool) {
	id := t.Find(fingerprint)

	if id != nil {
		id.MfaEnabled = mfaEnabled
		id.MfaNeeded = mfaNeeded
		id.CId.MfaEnabled = mfaEnabled
		id.CId.MfaNeeded = mfaNeeded
	}
}

func (t *RuntimeState) UpdateControllerAddress(configFile string, newAddress string) {
	log.Debugf("request to update config file %s with new address: %s", configFile, newAddress)

	f, fe := ioutil.ReadFile(configFile)
	if fe != nil {
		log.Warnf("Could not read identity file: %s", configFile)
		return
	}
	c := idcfg.Config{}
	err := json.Unmarshal(f, &c)
	if err != nil {
		log.Warnf("Could not unmarshal config file for identity file: %s to newAddress: %s", configFile, newAddress)
		return
	}

	if strings.Compare(c.ZtAPI, newAddress) == 0 {
		log.Debugf("not updating config for identity file %s. address already set to: %s", newAddress)
		return
	}

	err = saveOriginalIdentity(configFile)
	if err != nil {
		log.Warnf("unexpected error when saving original identity. cannot change controller address. %v", err)
		return
	}

	newConfigFileName := configFile + ".address.update"
	defer func() {
		log.Debugf("removing original file after update: %s", newConfigFileName)
		os.Remove(newConfigFileName)
	}()
	log.Debugf("renaming identity file %s as: %s", configFile, newConfigFileName)
	_ = os.Rename(configFile, newConfigFileName)

	var newAddy string
	if strings.HasPrefix(newAddress, "https://") {
		newAddy = newAddress
	} else {
		newAddy = "https://" + newAddress
	}
	log.Infof("updating identity file %s with new address. changing from %s to %s", configFile, c.ZtAPI, newAddy)
	c.ZtAPI = newAddy

	idFile, err := os.OpenFile(configFile, os.O_RDWR|os.O_CREATE|os.O_TRUNC, 0644)
	defer idFile.Close()
	if err != nil {
		log.Warnf("An unexpected error has occurred while trying to update identity file %s with newAddress %s. %v", configFile, newAddress, err)
		return
	}

	w := bufio.NewWriter(bufio.NewWriter(idFile))
	enc := json.NewEncoder(w)
	_ = enc.Encode(c)
	_ = w.Flush()

	err = idFile.Close()
	if err != nil {
		log.Warnf("An unexpected error has occurred while closing the identity file %s with newAddress %s. %v", configFile, newAddress, err)
	}
}

// if a change address header is ever processed - archive the original identity used. it will never be overwritten once created
// it will be deleted when the identity is forgotten
func saveOriginalIdentity(configFile string) error {
	originalFileName := configFile + ".original"

	_, err := os.Stat(originalFileName)
	if err != nil {
		if os.IsNotExist(err) {
			//file does not exist. good...
		} else {
			return err
		}
	} else {
		log.Debugf("original identity already exists. not overwriting")
		return nil
	}

	log.Debugf("renaming original identity file from %s to %s", configFile, originalFileName)
	return os.Rename(configFile, originalFileName)
}

func (t *RuntimeState) SetNotified(fingerprint string, notified bool) {
	id := t.Find(fingerprint)

	if id != nil {
		id.Notified = notified
	}
}

func (t *RuntimeState) UpdateNotificationFrequency(notificationFreq int) error {

	log.Infof("setting notification frequency : %d", notificationFreq)

	if notificationFreq < constants.MinimumFrequency || notificationFreq > constants.MaximumFrequency {
		return errors.New(fmt.Sprintf("Notification frequency should be between %d and %d minutes", constants.MinimumFrequency, constants.MaximumFrequency))
	}

	rts.state.NotificationFrequency = notificationFreq

	rts.SaveState()

	return nil
}

func CleanUpZitiTUNAdapters(tunName string) {
	log.Info("Invoking ZitiTun adapter cleanup script")
	tun.WintunPool.DeleteMatchingAdapters(func(wintun *wintun.Adapter) bool {
		interfaceName, err := wintun.Name()
		if err != nil {
			log.Warnf("Could not determine interface name, not removing: %v", err)
			return false
		}
		if strings.HasPrefix(interfaceName, tunName) {
			log.Infof("Removing old Wintun interface with name : %s", interfaceName)
			return true
		}
		return false
	}, false)
}

# Electricity tariff comparison

## Installation

### Windows

* Extract all the files from the zip file into a folder of your choice.
* Open a command prompt as administrator (in your start menu, search for "CMD", right click it, and choose "Run as Administrator").
* In the command prompt, register the port Electricity uses - type <sup id="a1">[*](#f1)</sup> **netsh http add urlacl url=http://+:8080/ user=Everyone**
* Close the command prompt.
* Open Windows Explorer and navigate to your chosen folder.
* If you wish to set up a shortcut in the start menu, right click on Electricity.exe and choose "Pin to Start".
* Start the program by double-clicking on it (or on your shortcut).
* The program will create a SQLite database in C:\ProgramData\Electricity, start up, and open your web browser at the Home page.

<b id="f1">*</b> The **8080** here is the port on which the web server will listen. You can change this if you like, but you must also include a **Port=** line in the config file <sup id="a1">[*](#f3)</sup>.

### Linux/Mac

* Extract all the files from the zip file into a folder of your choice.
* Start the program by double-clicking on it.
* The program will create an SQLite database un /usr/share/AccountServer and start up.
* Open your web browser, and navigate to <sup id="a2">[*](#f1)</sup> **http://localhost:8080/**

<b id="f2">*</b> The 8080 here is the port on which the web server is listening.

## Every day running

The program runs as a web server. While it is running, you can connect to it from any web server with access to your network (including phones and tablets which are on the same wireless network). The URL to connect is **http://localhost:8080/**, but with **localhost** replaced by the name or IP address of your computer. It is OK to leave the package running all day, and/or to add it to your startup group so it runs automatically when you log on. It could be run as a service (so it runs all the time your computer is switched on, even if you are not logged on), but this is not implemented (and would be different depending on whether you are running Linux or Windows).

If you do leave the package running all the time, it would be a good idea to create a bookmark to it in your web browser for ease of access.

Note that the Google Chrome browser gives the best user experience with this package. It can run in any browser, but most other browsers do not support HTML5 as well (e.g. by offering drop-down calendars for dates).

## Importing data


I get my smart meter data from https://data.n3rgy.com/consumer/home
All you need to do is to give it the number off your smart meter repeater box, and you can download half hourly data.

There is an import button in the Electricity app which allows you to import it.

This gives you a zip file containing your data - extract the csv file (mine is in electricity/consumption/1), then click the Import button, pick the file, and click Save to import it.

Be aware that it is best to import about 1 month's data at a time, as the import seems to struggle with very large files.

The program will also cope with data downloaded from Octopus Energy. If you have data in other formats that doesn't import, please raise an issue on github, and include the headings and a short selection of data from the import file.

## Adding tariff Scenarios

- Click New Scenario
- Fill in a name, the normal rate and standing charge (both in pence)
- If there are different tariffs at different times of day, fill then in at the bottom. Fill in the start and end times as decimal numbers, representing hours and minutes in the 24 hour clock - e.g. 01:30 am is 1.30, 4:15 pm is 16.00. Put the rate in (in pence per unit). If you think you can shift your electricity usage into a cheap period, enter the number of units per week you can switch from normal rate to this rate.
- Once you enter a rate, another blank rate appears, in case you have more than 1 cheap (oe even expensive) rate.
- Enter the time period you want to check, and click Save
- The information will be calculated from your imported data, and appear on the form. Note, if you don't have data for the full range you requested, the period start and end will be updated to reflect the period for which you do have data.

You can add as many scenarios as you like. There is a copy button to make a new copy of an existing scenario.

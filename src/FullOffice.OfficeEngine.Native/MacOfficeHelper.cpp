/*
 * (c) Copyright Ascensio System SIA 2010-2019
 *
 * This program is a free software product. You can redistribute it and/or
 * modify it under the terms of the GNU Affero General Public License (AGPL)
 * version 3 as published by the Free Software Foundation. In accordance with
 * Section 7(a) of the GNU AGPL its Section 15 shall be amended to the effect
 * that Ascensio System SIA expressly excludes the warranty of non-infringement
 * of any third-party rights.
 *
 * This program is distributed WITHOUT ANY WARRANTY; without even the implied
 * warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR  PURPOSE. For
 * details, see the GNU AGPL at: http://www.gnu.org/licenses/agpl-3.0.html
 *
 * You can contact Ascensio System SIA at 20A-12 Ernesta Birznieka-Upisha
 * street, Riga, Latvia, EU, LV-1050.
 *
 * The  interactive user interfaces in modified source and object code versions
 * of the Program must display Appropriate Legal Notices, as required under
 * Section 5 of the GNU AGPL version 3.
 *
 * Pursuant to Section 7(b) of the License you must retain the original Product
 * logo when distributing the program. Pursuant to Section 7(e) we decline to
 * grant you any rights under trademark law for use of our trademarks.
 *
 * All the Product's GUI elements, including illustrations and icon sets, as
 * well as technical writing content are licensed under the terms of the
 * Creative Commons Attribution-ShareAlike 4.0 International. See the License
 * terms at http://creativecommons.org/licenses/by-sa/4.0/legalcode
 *
*/

#include "applicationmanager.h"
#include <cstdio>
#include <cstring>
// Adapted from pinned upstream desktop-sdk helper_main.cpp; see provenance below.
// https://github.com/ONLYOFFICE/desktop-sdk/blob/6bd2051287a496ac1504214caca69a5729625696/ChromiumBasedEditors/lib/helper_main.cpp

int main( int argc, char *argv[] )
{
#ifdef _WIN32
	Core_SetProcessDpiAwareness();

	// Set non-browser processes up to be killed by the system after the
	// browser goes away. The browser uses the default shutdown order, which
	// is 0x280. Note that lower numbers here denote "kill later" and higher
	// numbers mean "kill sooner". This gets rid of most of those unsightly
	// sad tabs on logout and shutdown.
	const int kNonBrowserShutdownPriority = 0x280;
	::SetProcessShutdownParameters(kNonBrowserShutdownPriority - 1, SHUTDOWN_NORETRY);

#endif

	CApplicationCEF application;
	CAscApplicationManager manager;

	const char* role="unknown";
	for(int i=1;i<argc;++i) if(std::strncmp(argv[i],"--type=",7)==0) role=argv[i]+7;
	std::fprintf(stderr,"FULL_OFFICE_HELPER_START role=%s\n",role);
	const int result=application.Init_CEF(&manager, argc, argv);
	std::fprintf(stderr,"FULL_OFFICE_HELPER_EXIT role=%s code=%d\n",role,result);
	return result;
}

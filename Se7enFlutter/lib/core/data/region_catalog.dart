import '../models/connection_method.dart';
import '../models/region_option.dart';

class RegionCatalog {
  RegionCatalog._();

  static const Map<String, String> names = {
    'AE': 'United Arab Emirates',
    'AR': 'Argentina',
    'AT': 'Austria',
    'AU': 'Australia',
    'BE': 'Belgium',
    'BG': 'Bulgaria',
    'BR': 'Brazil',
    'CA': 'Canada',
    'CH': 'Switzerland',
    'CL': 'Chile',
    'CN': 'China',
    'CO': 'Colombia',
    'CY': 'Cyprus',
    'CZ': 'Czechia',
    'DE': 'Germany',
    'DK': 'Denmark',
    'EE': 'Estonia',
    'EG': 'Egypt',
    'ES': 'Spain',
    'FI': 'Finland',
    'FR': 'France',
    'GB': 'United Kingdom',
    'GR': 'Greece',
    'HK': 'Hong Kong',
    'HR': 'Croatia',
    'HU': 'Hungary',
    'ID': 'Indonesia',
    'IE': 'Ireland',
    'IL': 'Israel',
    'IN': 'India',
    'IS': 'Iceland',
    'IT': 'Italy',
    'JP': 'Japan',
    'KR': 'South Korea',
    'LT': 'Lithuania',
    'LU': 'Luxembourg',
    'LV': 'Latvia',
    'MD': 'Moldova',
    'MX': 'Mexico',
    'MY': 'Malaysia',
    'NL': 'Netherlands',
    'NO': 'Norway',
    'NZ': 'New Zealand',
    'PH': 'Philippines',
    'PL': 'Poland',
    'PT': 'Portugal',
    'RO': 'Romania',
    'RS': 'Serbia',
    'RU': 'Russia',
    'SE': 'Sweden',
    'SG': 'Singapore',
    'SI': 'Slovenia',
    'SK': 'Slovakia',
    'TH': 'Thailand',
    'TR': 'Türkiye',
    'TW': 'Taiwan',
    'UA': 'Ukraine',
    'US': 'United States',
    'VN': 'Vietnam',
    'ZA': 'South Africa',
  };

  static const List<String> psiphonSeedRegions = [
    'AT', 'AU', 'BE', 'CA', 'CH', 'CZ', 'DE', 'DK', 'ES', 'FI', 'FR',
    'GB', 'ID', 'IE', 'IN', 'IT', 'JP', 'LT', 'NL', 'NO', 'PL', 'RO',
    'RS', 'SE', 'SG', 'US',
  ];

  static const List<String> torSeedRegions = [
    'AT', 'CA', 'CH', 'CZ', 'DE', 'DK', 'ES', 'FI', 'FR', 'GB',
    'HU', 'IE', 'IS', 'IT', 'JP', 'LU', 'LV', 'NL', 'NO', 'PL',
    'PT', 'RO', 'RS', 'SE', 'SG', 'SK', 'UA', 'US',
  ];

  static const List<String> seedRegionCodes = psiphonSeedRegions;

  static String nameFor(String code) =>
      names[code.toUpperCase()] ?? code.toUpperCase();

  static List<RegionOption> optionsFrom(Iterable<String> codes) {
    final effectiveCodes = codes.isNotEmpty ? codes : psiphonSeedRegions;
    final list = effectiveCodes
        .map((c) => c.trim().toUpperCase())
        .where((c) => c.isNotEmpty)
        .toSet()
        .map((c) => RegionOption(c, nameFor(c)))
        .toList()
      ..sort((a, b) => a.name.compareTo(b.name));
    return [const RegionOption('', 'Best / Auto'), ...list];
  }

  static List<String> seedForMethod(ConnectionMethod method) {
    if (method.isTor) return torSeedRegions;
    return psiphonSeedRegions;
  }
}

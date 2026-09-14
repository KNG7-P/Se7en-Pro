
class RegionOption {
  const RegionOption(this.code, this.name);
  final String code;
  final String name;

  bool get isAuto => code.isEmpty;
}
